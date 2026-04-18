# hg5c_cam – Implementációs megjegyzések és döntések

## 1. Implementáció részletei

- A PlayerService fűzi össze a teljes RTSP URL-t a beállításokból.
- Ha az URL argumentumokban jelszó is van, az első felülírja a mentett jelszót.
- .NET 8 target legyen használva.

---

## 2. Ismert technikai korlátok és kerülő megoldások

### FFmpeg.AutoGen és unsafe kód
- Az `FFmpeg.AutoGen` wrapper unsafe pointer-eket használ; a `.csproj`-ban szükséges az `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` beállítás.
- Az FFmpeg natív DLL-eket (`avcodec`, `avformat`, `avutil`, `swscale`, `swresample`) az exe mellé kell másolni; a keresési útvonalat a `FFmpegBinariesHelper` vagy `ffmpeg.RootPath` beállításával kell megadni induláskor.

### D3DImage és szálkezelés
- A `D3DImage` csak az UI szálon érhető el (`Lock` / `AddDirtyRect` / `Unlock`).
- A dekódolási hurok háttérszálon fut; minden frame után `Dispatcher.BeginInvoke`-kal kell az UI szálra marshalolni a `D3DImage` frissítését.
- A Direct3D 9 device-t `CreateFlags.Multithreaded` flaggel kell létrehozni, különben a háttérszálból való surface-írás és az UI szálról való olvasás versenyhelyzetet okoz.

### D3DImage és ablakméret-változás
- Ha az ablakot átméretezik és a videó felbontása megváltozik (stream újracsatlakozáskor más felbontással jön), a D3D surface-t és a `D3DImage` back buffert újra kell inicializálni az új mérettel.
- Ezt a `PlayerService`-ben kell kezelni: felbontásváltozáskor `SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero)` hívással le kell választani a régi surface-t, majd az újat beállítani.

### Stream FPS lekérdezése
- Az FFmpeg `AVStream.avg_frame_rate` adja a névleges FPS-t; ez a `avformat_find_stream_info` után elérhető.
- Ha az FPS ismeretlen vagy nulla, az alapértelmezett 30 FPS-t kell használni a MaxFps ellenőrzéséhez.
- A MaxFps korlátozás szoftveresen valósítható meg a dekódolási hurokban: frame-ek kihagyásával vagy `Thread.Sleep`-pel a célzott frame időközökhöz igazítva.

### RTSP TCP transport
- Alapértelmezetten az FFmpeg UDP-n próbálja megnyitni az RTSP streamt, ami tűzfalon vagy NAT-on mögött megbízhatatlan lehet.
- Megoldás: az `avformat_open_input` előtt az `AVDictionary`-ba kell beírni: `rtsp_transport = tcp`.

### Reconnect
- A dekódolási hurok `av_read_frame` hívása negatív visszatérési értékkel jelez hibát (pl. `AVERROR_EOF`, `AVERROR(ETIMEDOUT)`).
- Az újraindítást `Task.Delay` + `Dispatcher.BeginInvoke`-kal kell az UI szálra marshalolni az állapotjelzéshez; maga az újraindítás háttérszálon történhet.

### Hang (NAudio)
- Az FFmpeg audió streamet szintén a dekódolási hurokban kell feldolgozni (`avcodec_receive_frame` az audió codecre).
- A dekódolt PCM adatokat egy `BufferedWaveProvider`-be kell írni, amelyet egy `WaveOut` játszik le.
- Ha a hang le van tiltva, az audió stream packet-jeit egyszerűen el kell dobni (`av_packet_unref`).

### Registry és multi-instance
- A PID-alapú slot-foglalás race condition-t okozhat, ha két példány egyszerre indul.
- **Megoldás**: Named Mutex használata a slot-foglalás körül (`Mutex("Global\\hg5c_cam_slot_lock")`).

### Ablakméret mentése
- `SizeChanged` esemény nagyon sűrűn érkezik átméretezés közben.
- **Megoldás**: 500 ms-os `DispatcherTimer` debounce – csak akkor ír Registry-be, ha 500 ms-ig nem változott a méret.

### Fullscreen és taskbar
- `WindowStyle.None` + `WindowState.Maximized` kombináció fullscreen-szerű megjelenést ad, de a tálca felett marad.

### Más ablak feletti megjelenés
- `Topmost = true`-val megoldható.

### Nullable tuples
- Ha egy tuple nullable, a `.Value`-n keresztül érd el a mezőket, hogy ne legyen fordítási hibaüzenet.

---

## 3. XAML namespace deklarációk (MainWindow.xaml)

A `D3DImage` a `System.Windows.Interop` névtérből érhető el, amelyet csak a code-behind-ban kell használni.

```xml
<!-- A videóterület egy egyszerű Image control -->
<Image x:Name="VideoImage" Stretch="Uniform" RenderOptions.BitmapScalingMode="NearestNeighbor"/>
```

---

## 4. .csproj konfiguráció

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Platforms>x64</Platforms>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <AssemblyName>hg5c_cam</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="FFmpeg.AutoGen" Version="6.*" />
    <PackageReference Include="SharpDX" Version="4.2.0" />
    <PackageReference Include="SharpDX.Direct3D9" Version="4.2.0" />
    <PackageReference Include="NAudio" Version="2.*" />
  </ItemGroup>
</Project>
```

---

## 5. Megvalósítási sorrend (javasolt)

1. **Project scaffold**: Solution, csproj, NuGet csomagok, alap App.xaml, a docs könyvtár is kerüljön a projectbe, hogy a Visual Studio-ból könnyen szerkeszthető legyen, a fordításból legyenek kizárva (implementation notes, architecture, requirements).
2. **RegistryService**: Instance slot logika, AppSettings mentés/olvasás, URL-előzmények.
3. **FFmpeg inicializálás**: DLL útvonal beállítás, `avformat`/`avcodec` regisztráció, RTSP megnyitás tesztelése.
4. **PlayerService alapja**: dekódolási hurok háttérszálon, `D3DImage` + Direct3D 9 surface inicializálás.
5. **MainWindow alapja**: `Image` control `D3DImage` source-szal, menüsor, ablakméret kezelés.
6. **SetupDialog**: Mezők, validáció, URL legördülő.
7. **Overlay-ek**: Connecting... / Disconnected státusz overlay, FPS overlay.
8. **Hang**: NAudio `WaveOut` + FFmpeg audió dekódolás.
9. **Fullscreen**: Dupla kattintás kezelése.
10. **Parancssori argumentum**: feldolgozás App.xaml.cs-ben.
11. **Build és tesztelés**: `dotnet publish`, FFmpeg DLL-ek másolása, self-contained csomag.

---

## 6. Tesztelési szempontok

- **Multi-instance**: Egyszerre 2-3 példány futtatása, külön URL-lel → ne zavarják egymást a Registry-ben.
- **Reconnect**: Stream URL leállítása közben → elhomályosított kép + Connecting... megjelenik → stream visszakapcsolás → automatikusan folytatódik.
- **Disconnected**: Megszakadt kapcsolat és lejárt próbálkozások esetén → kiugrás fullscreenből, ha úgy vagyunk → elhomályosított kép + Disconnected... megjelenik.
- **Ablakméret**: Kilépés és újraindítás → az ablak az előző méretben és pozícióban jelenik meg.
- **Első indítás**: Tiszta Registry → Setup dialógus automatikusan felugrik.
- **Setup**: Van változás a UI-on a mentett állapothoz képest → aktív a Cancel gomb. Ha nincs → szürke, tiltott.
- **URL history**: 10-nél több URL megadása → a legrégebbi kiesik.
- **Fullscreen**: Dupla kattintás → fullscreen; dupla kattintás → visszaáll.
- **Parancssori**: `hg5c_cam.exe "rtsp://..."` → az URL megjelenik a Setup-ban és az előzményekben.
- **Felbontásváltás**: Reconnect után eltérő felbontású stream → D3D surface újrainicializálódik, kép helyesen jelenik meg.
