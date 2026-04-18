# hg5c_cam – Architektúra és projektstruktúra

## 1. Technológiai stack

| Komponens | Választás | Indoklás |
|-----------|-----------|----------|
| UI framework | WPF .NET 8 targettel | Érett, stabil, D3DImage támogatás natívan |
| Video lejátszás | FFmpeg.AutoGen + D3DImage | RTSP támogatás, minimális memória- és CPU-igény; a dekódolt frame közvetlenül GPU-memóriában marad |
| Target | A Windows-nak a .NET 8 által támogatott minimum verziója, x64 | Self-contained build, .NET runtime nem szükséges a célgépen |
| Konfiguráció tárolása | Windows Registry (HKCU) | Egyszerű, beépített Windows API, multi-instance-barát |
| Build | .NET 8 SDK, `dotnet publish` | Single-folder, self-contained, x64 |

---

## 2. Projektstruktúra

```
hg5c_cam/
├── hg5c_cam.sln
├── hg5c_cam/
│   ├── hg5c_cam.csproj
│   ├── App.xaml
│   ├── App.xaml.cs
│   ├── MainWindow.xaml
│   ├── MainWindow.xaml.cs
│   ├── Dialogs/
│   │   ├── SetupDialog.xaml
│   │   └── SetupDialog.xaml.cs
│   ├── Services/
│   │   ├── RegistryService.cs       – Registry olvasás/írás, instance slot kezelés
│   │   ├── PlayerService.cs         – FFmpeg init, RTSP dekódolás, D3DImage frissítés, reconnect
│   │   └── FpsCounterService.cs     – FPS számítás, memóriafoglalás és overlay frissítés
│   ├── Models/
│   │   ├── AppSettings.cs           – Egy instance beállításainak modellje
│   │   └── InstanceSlot.cs          – Registry slot azonosítás és PID kezelés
│   ├── Converters/
│   │   └── BoolToVisibilityConverter.cs
│   └── Assets/
│       └── hg5c_cam.ico
└── docs/
    ├── REQUIREMENTS.md
    ├── ARCHITECTURE.md              – ez a fájl
    └── IMPLEMENTATION_NOTES.md
```

- A Visual Studio projectben, a Project Managerben a "docs" mappa tartalma, vagyis az md fájlok kerüljenek egy Documents nevű virtuális folderbe.
- Ha a "{Dialogs,Services,Models,Converters,Assets}" folderre nincs szükség, ne legyen létrehozva.

---

## 3. Komponensek leírása

### App.xaml.cs
- Parancssori argumentum feldolgozás.
- Instance slot foglalás indításkor (RegistryService).
- Slot felszabadítás alkalmazás kilépéskor.

### MainWindow.xaml / .cs
- Főablak: menüsor + `Image` control, amelynek forrása egy `D3DImage`.
- Ablakméret változás figyelése → Registry mentés (debounce-szal, hogy ne írjon minden pixelnél).
- Dupla kattintás kezelése → fullscreen váltás.
- Connecting overlay: `Grid` réteg a videóterület felett, láthatósága kötött a player állapotához.
- FPS és memória overlay: `TextBlock` a videóterület bal alsó sarkában.
- Menüsor kezelése.

### SetupDialog.xaml / .cs
- Modális dialógus.
- URL legördülő: `ComboBox` szabad szöveges bevitellel (`IsEditable="True"`).
- Spinner mezők: `NumericUpDown` (WPF-ben custom control vagy `TextBox` + validáció).
- OK gomb: validáció → mentés → dialógus bezárása. Csak akkor aktív, ha van változás a UI elemekben a tároltakhoz képest.
- Cancel gomb: mindig aktív.

### RegistryService.cs
Felelős:
- Instance slot keresés és foglalás (PID alapú).
- Slot felszabadítás kilépéskor.
- `AppSettings` olvasása/írása a saját Instance slot-ba.
- Közös URL-előzmények olvasása/írása (`Shared\UrlHistory`).

Főbb metódusok:
```csharp
int AcquireInstanceSlot();             // visszaadja az N-t (Instance_N)
void ReleaseInstanceSlot(int slot);
AppSettings LoadSettings(int slot);
void SaveSettings(int slot, AppSettings settings);
List<string> LoadUrlHistory();
void SaveUrlHistory(List<string> urls);
void AddUrlToHistory(string url);      // max 10, FIFO
```

### PlayerService.cs
Felelős:
- FFmpeg.AutoGen inicializálás (`avformat`, `avcodec`, `swscale`).
- RTSP stream megnyitása (`avformat_open_input` TCP transport réteggel).
- Háttérszálon futó dekódolási hurok: `av_read_frame` → `avcodec_send_packet` → `avcodec_receive_frame`.
- Minden dekódolt frame konvertálása `AV_PIX_FMT_BGRA` formátumba (`sws_scale`), majd átmásolása a megosztott Direct3D textúrába.
- `D3DImage.Lock / AddDirtyRect / Unlock` meghívása az UI szálon (`Dispatcher.BeginInvoke`) minden új frame után.
- Reconnect logika: hiba vagy stream vége esetén delay után újraindítja a dekódolási hurkot.
- `PlayerState` property: `Playing`, `Connecting`, `Stopped`, `Disconnected`.
- Hang lejátszás: `NAudio` (WaveOut + FFmpeg audió dekódolás), vagy letiltható.

Főbb metódusok:
```csharp
void Initialize(D3DImage d3dImage);
void Play(string rtspUrl, int maxFps, int reconnectDelaySec, int retries, bool soundEnabled);
void Stop();
float? GetStreamFps();
event Action<PlayerState> StateChanged;
```

### FpsCounterService.cs
- `DispatcherTimer` alapú, 1 másodpercenként frissít.
- A `PlayerService`-től kap frame-számlálót (Interlocked.Increment alapú).
- `CurrentFps` property, változáskor eseményt dob.
- Memóriahasználat lekérdezése: `Process.GetCurrentProcess().PrivateMemorySize64`. Változáskor eseményt dob.

### AppSettings.cs
```csharp
public class AppSettings
{
    public string Url { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public int PortNumber { get; set; } = 554;
    public int ReconnectDelaySec { get; set; } = 3;
    public int MaxFps { get; set; } = 0;          // 0 = nincs limit
    public bool ShowFpsOverlay { get; set; } = false;
    public bool TopmostWindow { get; set; } = false;
    public bool SoundEnabled { get; set; } = true;
    public int? WindowLeft { get; set; }
    public int? WindowTop { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
}
```

---

## 4. Fontosabb WPF / FFmpeg / D3DImage megjegyzések

### D3DImage és overlay
- A WPF `D3DImage` egy `ImageSource`, amelyet egy `Image` control `Source`-aként kell beállítani.
- A videóterületet egy `Grid`-be kell helyezni; az overlay elemek (FPS, Connecting...) ugyanabban a `Grid`-ben, magasabb z-sorrendben helyezkednek el.
- Ez a megközelítés **nem ütközik HwndHost airspace-problémába**, így a WPF overlay-ek szabadon rajzolhatók a videó fölé hagyományos z-order-rel.

```xml
<Grid>
    <Image x:Name="VideoImage" Stretch="Uniform"/>
    <!-- FPS overlay -->
    <Border x:Name="FpsOverlayBorder" HorizontalAlignment="Left" VerticalAlignment="Bottom" ...>
        <TextBlock x:Name="FpsOverlayText" .../>
    </Border>
    <!-- Status overlay -->
    <Grid x:Name="StatusOverlay">
        <Rectangle Fill="Black" Opacity="0.55"/>
        <TextBlock x:Name="StatusText" Text="Connecting..." .../>
    </Grid>
</Grid>
```

### D3DImage inicializálás (PlayerService)
```csharp
// Direct3D 9 device létrehozás (SharpDX)
var d3d = new Direct3D();
var device = new Device(d3d, 0, DeviceType.Hardware, IntPtr.Zero,
    CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded,
    new PresentParameters { Windowed = true, SwapEffect = SwapEffect.Discard });

// Shared surface
var surface = Surface.CreateRenderTarget(device, width, height,
    Format.X8R8G8B8, MultisampleType.None, 0, true);

_d3dImage.Lock();
_d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surface.NativePointer);
_d3dImage.Unlock();
```

### Frame frissítés UI szálon
```csharp
// Háttérszálról hívva:
Application.Current.Dispatcher.BeginInvoke(() =>
{
    _d3dImage.Lock();
    // A surface-t itt már feltöltötte a dekódoló szál sws_scale-lel
    _d3dImage.AddDirtyRect(new Int32Rect(0, 0, _d3dImage.PixelWidth, _d3dImage.PixelHeight));
    _d3dImage.Unlock();
});
```

### Utolsó frame elhomályosítva
- A D3DImage a legutóbb kirajzolt frame-et tartja mindaddig, amíg új frame nem érkezik.
- Az elhomályosítást egy félátlátszó fekete `Rectangle` overlay adja (pl. `Opacity="0.5"`), amelyet a `StatusOverlay` `Grid` tartalmaz.

### Arányos átméretezés
- Az `Image` control `Stretch="Uniform"` beállítással arányosan tölti ki a rendelkezésre álló területet (letterbox/pillarbox automatikusan).

### Fullscreen
```csharp
private WindowState _previousState;
private WindowStyle _previousStyle;

void EnterFullscreen() {
    _previousState = WindowState;
    _previousStyle = WindowStyle;
    WindowStyle = WindowStyle.None;
    WindowState = WindowState.Maximized;
    MenuBar.Visibility = Visibility.Collapsed;
}

void ExitFullscreen() {
    WindowStyle = _previousStyle;
    WindowState = _previousState;
    MenuBar.Visibility = Visibility.Visible;
}
```

### Ablakméret mentés debounce-szal
- `SizeChanged` eseményre figyelés, de csak 500 ms inaktivitás után ír Registry-be (`DispatcherTimer` reset).

---

## 5. Build és terjesztés

### Build parancs
```bash
dotnet publish hg5c_cam/hg5c_cam.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=false ^
  -o ./publish/hg5c_cam
```

> `PublishSingleFile=false` azért, mert az FFmpeg natív DLL-eket nem lehet egyetlen exe-be csomagolni. A kimenet egy mappa lesz.

### A publish mappa tartalma (hozzávetőleg)
```
hg5c_cam/
├── hg5c_cam.exe
├── hg5c_cam.dll
├── avcodec-61.dll
├── avformat-61.dll
├── avutil-59.dll
├── swscale-8.dll
├── swresample-5.dll
└── [.NET runtime DLL-ek, ~50-100 fájl]
```

### NuGet csomagok (hg5c_cam.csproj)
```xml
<PackageReference Include="FFmpeg.AutoGen" Version="6.*" />
<PackageReference Include="SharpDX" Version="4.2.0" />
<PackageReference Include="SharpDX.Direct3D9" Version="4.2.0" />
<PackageReference Include="NAudio" Version="2.*" />
```

> Az FFmpeg natív DLL-eket (`avcodec`, `avformat`, `avutil`, `swscale`, `swresample`) külön kell a publish mappába másolni; ezek nem NuGet-ből jönnek. Letöltési forrás: https://github.com/BtbN/FFmpeg-Builds/releases (win64, lgpl, shared build).
