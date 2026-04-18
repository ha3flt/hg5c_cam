# hg5c_cam – Funkcionális követelmények

## 1. Áttekintés

Windows asztali alkalmazás RTSP kamerastream lejátszásához.  
Technológia: **WPF, .NET 8, 64-bit, FFmpeg + D3DImage**  
Kimeneti fájl: `hg5c_cam.exe` (self-contained mappa)
A programban csak angol kiírt szövegek, nevek, programbeli megjegyzések stb. szerepeljenek, semmilyen magyar szöveg se.

---

## 2. Indítás és parancssori argumentumok

- A program futtatható parancssori argumentum nélkül és argumentummal is.
- Argumentum nélküli futtatásnál egy példány indul el a programból.
- Argumentum nélküli futtatásnál ha nincs érvényes mentett konfiguráció, a Setup dialógus automatikusan megjelenik.
- Argumentum nélküli futtatásnál ha van már érvényes mentett konfiguráció, a program azzal indul el, és a Setup dialógus nem jelenik meg.
- A kapcsolók nélkül írt argumentumok URL-ként kerülnek értelmezésre.
- Támogatott argumentum: `[rtsp_url1] [rtsp_url2]` és így tovább.
  - Példa: `hg5c_cam.exe "rtsp://name:password@kkcnet.ddns.net:port1/stream" "rtsp://name:password@kkcnet.ddns.net:port2/stream"`
  - A teljes URL-ek automatikusan bekerülnek az URL-előzményekbe és az első alapértelmezett URL-ként jelenik meg a Setup dialógusban.
  - Név, jelszó és portszám is megadható az URL-ben, az első URL-ből kibontott név,jelszó és portszám mentésre kerül a beállítások közé.
  - Ha a név, jelszó vagy portszám szerepel az URL-ben, az első URL-ből kibontott név, jelszó, portszám mentésre kerül a beállítások közé.
  - A Setup dialógusban a név, jelszó, URL, port, FPS Overlay kapcsoló, a Max FPS és Reconnect Delay mezők szabadon szerkeszthetők és mentődnek a Registry-be.
  - Ha a név, jelszó vagy portszám szerepel a beállítások között, nem üresek, akkor felülírják az URL-ben szereplő értékeket, amikor az RTSP URL-t a program előállítja.
  - Ha a portszám nincs kitöltve sem az URL-ben, sem a beállítások között, akkor az alapértelmezett RTSP port (554) kerül az RTSP URL-be (ahol mindenképpen megadjuk a portszámot).
  - A Setup dialógusban az URL mező legördülő listaként jelenik meg, ahol az utolsó 10 használt URL szerepel, és szabad szöveges bevitel is lehetséges.
  - A Setup dialógusban a legördülő URL mezőből ha kiválasztok egy elemet, akkor a név, jelszó és port mezők kitöltődnek a kiválasztott URL-ben szereplő értékekkel (ha vannak).
  - A Setup dialógusban az URL mező kiírt alapértéke ez, amikor üres: "rtsp://"
- A program több példányban is futhat egyidejűleg, egymástól függetlenül. Minden megadott url-hez egy új példány indul, és minden példány a saját beállításait használja a Registry-ben (Instance_N slot).

---

## 3. Registry-struktúra

Gyökér: `HKEY_CURRENT_USER\Software\hg5c_cam\`

```
HKCU\Software\hg5c_cam\
    Shared\
        UrlHistory\
            0   (REG_SZ) – legutóbbi URL
            1   (REG_SZ)
            ...
            9   (REG_SZ) – legtávolabbi URL (max. 10 bejegyzés)
    Instance_1\
        WindowLeft      (REG_DWORD)
        WindowTop       (REG_DWORD)
        WindowWidth     (REG_DWORD)
        WindowHeight    (REG_DWORD)
        TopMost         (REG_DWORD)  – 0 vagy 1
        Url             (REG_SZ)
        Username        (REG_SZ)
        Password        (REG_SZ)
        Port            (REG_DWORD)
        ReconnectDelay  (REG_DWORD)  – másodpercben, alapértelmezett: 3
        MaxFps          (REG_DWORD)  – alapértelmezett: 0 (= stream FPS, vagy 30 ha ismeretlen)
        ShowFpsOverlay  (REG_DWORD)  – 0 vagy 1
    Instance_2\
        ...
```

### Multi-instance logika

- Indításkor a program megkeresi az első szabad `Instance_N` slot-ot (ahol N = 1, 2, 3, ...).
- "Szabad" slot: nem létezik, vagy nem tartalmaz érvényes process ID-t (a futó példány a saját PID-jét írja a slotba induláskor).
- Kilépéskor a program törli a saját `Instance_N` slotját (vagy csak a PID-et törli belőle, a többi beállítást meghagyja).
- Így a slot újrafelhasználható a következő indításkor, és az ablakméret/beállítások megmaradnak.

---

## 4. Főablak

### 4.1 Ablakméret és pozíció

- Indításkor a méret és pozíció a Registry-ből töltődik be (az adott Instance slot alapján).
- Ha a TopMost beállítás be van kapcsolva, az ablak tulajdonságai közt a TopMost bekapcsolásra kerül
- Indításkor a Disconnected overlay szöveg kerül kiírásra, amíg a streamhez el nem kezd a program kapcsolódni.
- Ha nincs mentett méret (új slot):
  - Az ablak méreteződjön a stream felbontására (pl. 1920×1080).
  - Ha ez meghaladná a képernyő 80%-át függőlegesen vagy vízszintesen, akkor arányosan kicsinyítse le, hogy elférjen.
  - Az ablak középre kerüljön a képernyőn.
- Ablakméret és pozíció változáskor automatikusan mentődik a Registry-be.
- Az ablak szabadon átméretezhető, maximalizálható és minimalizálható.
- Minimalizáláskor az alkalmazás a tálcán marad (nincs tray icon).

### 4.2 Videóterület

- A videó kitölti az ablak belső területét (menüsor alatt).
- Átméretezéskor (beleértve a maximalizálást) a videó arányosan skálázódik, hogy kitöltse az elérhető területet, de megtartsa az eredeti oldalarányt (letterbox/pillarbox módszer).
- Átméretezéskor az ablakméret mindig arányos legyen a videóval. Pl. ha vízszintesen átméretezem az ablakot, a függőleges méret is változzon vele együtt.
- Hang- és videólejátszás is van. A hang a default hangcsatornát használja. A hang a Setup dialógusban beállítottaktól függően van tiltva vagy engedélyezve.

### 4.3 Menüsor

```
  [File]       [View]             [Help]
  Settings     Stream sound ✓     Help
  ────────     Topmost window ✓   ─────
  Exit         FPS overlay ✓      About
```

- **Settings**: megnyitja a Setup dialógust.
- **Exit**: leállítja a lejátszást és bezárja az ablakot.
- **Stream sound**: ki/be kapcsolja a hangot (szinkronban van a Setup dialógus kapcsolójával). Ennek átállítása újraindítja a lejátszást, hogy a hangbeállítás érvénybe lépjen.
- **Topmost window**: ki/be kapcsolja a főablak TopMost tulajdonságát (szinkronban van a Setup dialógus kapcsolójával).
- **FPS overlay**: ki/be kapcsolja az FPS kijelzést (szinkronban van a Setup dialógus kapcsolójával).
- **Help**: megnyit egy üres Help dialógust, amiben a program kiemelt nevén kívül és az alatta, lejjebb levő "Under construction..." feliraton kívül más nem szerepel.
- **About**: megnyitja a Névjegy dialógust.

### 4.4 Fullscreen mód

- Dupla kattintásra a videóterületen lép fullscreen módba.
- Fullscreen módban a menüsor elrejtődik.
- Újabb duplakattintásra vagy az ESC gomb megnyomására visszaáll az előző ablakméretbe.
- Fullscreen módban is működik a Connecting... overlay.
- Sikertelenség esetén, ha már nem próbálkozik a program újracsatlakozással, automatikusan kilép a fullscreen módból, valamint a "Disconnected" overlay szöveg jelenik meg.

---

## 5. Setup dialógus

### Megjelenési feltételek

- Automatikusan felugrik induláskor, ha az URL mező üres, vagy nem üres, de nem tartalmazza a minimálisan szükséges információt: protokoll, név, jelszó, host.
- Manuálisan: Fájl → Beállítások menüpontból.

### Mezők

| Mező | Típus | Leírás |
|------|-------|--------|
| RTSP URL | Legördülő szövegmező | Az utolsó 10 URL listázva; szabad szöveges bevitel is lehetséges, Registry-be mentve, alapérték: rtsp:// |
| Name | Szövegmező | Plain text, Registry-be mentve |
| Password | Jelszómező | Plain text, Registry-be mentve |
| Port | Számmező | 1-65535, Registry-be mentve |
| Sound | Checkbox | Be/ki kapcsoló, Registry-be mentve, alapértelmezett: bekapcsolva |
| Max. FPS | Számmező (spinner) | Csak a változóméret korlátozza a számot (a Registry-ben DWORD lesz), alapértelmezett: 0 = stream FPS (ha ismeretlen: 30) |
| Connection delay | Szám mező (spinner) | csak a változóméret korlátozza a számot (a Registry-ben DWORD lesz), másodpercekben kifejezve, alapértelmezett: 3 |
| Connection retries | Szám mező (spinner) | Nullától felfelé, csak a változóméret korlátozza a számot (a Registry-ben DWORD lesz), alapértelmezett: 25 |
| Topmost window | Checkbox | Be/ki kapcsoló, alapértelmezett: kikapcsolva |
| FPS overlay | Checkbox | Be/ki kapcsoló |

Az RTSP URL, Name, Password, Port mezők csoportja legyen kis mértékben elkülönítve az utánuk jövő többi beállítás csoportjától (pl. kicsit nagyobb sorköz), majd azok az utolsó csoporttól, ami a Topmost Windows és az FPS overlay.

### Gombok

- **OK**: elmenti a beállításokat, bezárja a dialógust, elindítja/újraindítja a lejátszást (csak akkor válik engedélyezetté, ha változtattunk a beállításokon).
- **Cancel**: elveti a változtatásokat.

### URL-előzmények kezelése

- OK gomb megnyomásakor az aktuális URL bekerül a közös előzménylistára (Shared\UrlHistory), ha még nem szerepel ott.
- Ha már 10 bejegyzés van, a legrégebbi törlődik.

---

## 6. Lejátszás és reconnect logika

### Normál lejátszás

- Ha van formailag érvényes URL, a program indításkor rögtön elkezd kapcsolódni a streamhez.
- Először a Connecting... felirat kiíródik az overlay-re.
- RTSP kapcsolat: `rtsp://[username]:[password]@[host]:[port]/[path]`
  - Példa: `rtsp://name:password@kkcnet.ddns.net:554/stream`
- Ha a `MaxFps` > 0, a lejátszó legfeljebb ennyi képkockát jelenít meg másodpercenként.

### Kapcsolat megszakadása esetén

- Az utolsó kameraframe elhomályosítva megmarad a képernyőn (az FFmpeg dekóder az utolsó dekódolt frame-et megtartja; ez a `D3DImage` felületre kirajzolva marad, amíg új frame nem érkezik).
- Középen megjelenik a `Connecting...` felirat (fehér szöveg, félkövér, árnyékkal).
- A program automatikusan újrapróbálja a csatlakozást a beállított késleltetéssel.
- Az újracsatlakozási kísérletek száma korlátlan.
- A program a reconnect közben is leállítható (Fájl → Kilépés vagy ablak bezárása).

### FPS overlay

- Ha engedélyezve van, a videóra rajzolva jelenik meg (bal alsó sarok, félátlátszó háttérrel, egyetlen sorban).
- Az aktuális lejátszási FPS-t mutatja (pl. `FPS: 25`).
- És az aktuális memóriafoglalást is mutatja (pl. `Mem.: 11.3 MB`).

### További instrukciók

- A szerverhez kapcsolódáskor először kiíródik a "Connecting..." szöveg.
- Sikeres kapcsolat esetén eltűnik a szöveg és a képet látjuk.
- Sikertelen kapcsolat esetén kiíródik a "Disconnected" szöveg.
- Lekapcsolódáskor először kiíródik a "Disconnecting..." szöveg.

---

## 7.. Kilépés

- Ablak bezárásakor vagy Fájl → Kilépés esetén:
  1. A lejátszás leáll.
  2. Az ablakméret és pozíció mentődik a Registry-be.
  3. A saját Instance slot PID-mezője törlődik.
  4. Az alkalmazás bezárul.
