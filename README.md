# Umbra · IPTV for Windows

**English** · [Español](#español)

Native IPTV player for Windows (WPF + VLC). Sign in with your **Xtream Codes** details (server, username and password) or with your **M3U playlist**.

> Umbra is only a player. It does not provide any channels or content; you need your own IPTV subscription or playlist.

## Table of contents

- [Requirements](#requirements)
- [Installation](#installation)
- [Usage](#usage)
- [Interface](#interface)
- [Features](#features)
- [Keyboard shortcuts](#keyboard-shortcuts)
- [Data and privacy](#data-and-privacy)
- [Project structure](#project-structure)
- [Self-test](#self-test)

## Requirements

- Windows 10 or 11, 64-bit
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (to build from source)
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) (only to run a published build)
- Git

LibVLC is installed automatically through NuGet; you do **not** need to install VLC.

## Installation

### Option A: Build from source

1. Clone the repository:

   ```bash
   git clone https://github.com/Nogui23/Umbra.git
   cd Umbra
   ```

2. Check that .NET 9 is installed:

   ```bash
   dotnet --version
   ```

3. Restore dependencies and build:

   ```bash
   dotnet build Umbra.sln -c Release
   ```

4. Run the app:

   ```bash
   dotnet run --project src/Umbra.App/Umbra.App.csproj -c Release
   ```

### Option B: Publish a standalone folder

```bash
dotnet publish src/Umbra.App/Umbra.App.csproj -c Release -o publish
```

Then run `publish\Umbra.exe`. You can copy the `publish` folder anywhere (it needs the .NET 9 Desktop Runtime).

### Option C: Visual Studio

Open `Umbra.sln` in Visual Studio 2022 (17.12 or later) with the **.NET desktop development** workload, set `Umbra.App` as the startup project and press `F5`.

## Usage

1. **Add account**:
   - **Xtream Codes**: server URL (`http://server:port`), username and password.
   - **M3U playlist**: a URL or an `.m3u` file. An XMLTV EPG guide is optional.
   - If you paste a `get.php?username=…&password=…` link, the full Xtream API is used: movie and series details, guide and catch-up.
2. The app remembers your accounts. Passwords are encrypted with Windows Data Protection (DPAPI), for your Windows user only. Next time it opens directly on the last account you used.

## Interface

- **Single window**: the player opens inside the app, on top of the screen you were on, and closing it takes you back.
- **Fullscreen on start**. `F11` toggles fullscreen/windowed (can be disabled in Settings).
- **Monochrome design**: black, white and grays; the only color comes from posters and logos.
- **Responsive**: the UI scales to the resolution (1024×768 up to 4K) and compacts columns in narrow windows. You can make it larger or smaller in Settings.
- **Animations**: screen transitions, sliding navigation selector, smooth scrolling and fading posters. If Windows has animations turned off, the app does not use them either.

## Features

- **Home**: shortcuts to TV, movies and series, recently watched, favorites and new releases.
- **Live TV**:
  - Categories, search, favorites and recently watched channels.
  - Per-channel program guide and **catch-up** (watch past programs) on supported channels.
- **Movies and series**:
  - Posters, detail pages (synopsis, cast, duration, rating and trailer), favorites and "Continue watching".
  - Seasons and episodes. Watched items are marked and the next episode plays automatically.
- **Global search** across channels, movies and series (`Ctrl+F`).
- **VLC player**: audio tracks, subtitles (also from file), aspect ratio, volume, channel list for zapping and, on live TV, progress of the current program.
- **Parental control**: right-click a category to lock it with a PIN (default PIN is `0000`).
- **Account**: status, expiry date, connections and allowed formats. Content can be refreshed from here.
- **Settings**: fullscreen, UI size, channel format (TS or HLS), network buffer, GPU acceleration, guide time offset and User-Agent.

## Keyboard shortcuts

| Key | Action |
|---|---|
| F11 | Fullscreen / windowed |
| Esc | Back (closes the player or open detail page) |
| Ctrl+F | Search |
| Space | Pause / resume |
| F or double-click on video | Fullscreen / windowed |
| ↑ ↓ (TV) or Page Up / Page Down | Previous / next channel |
| ← → | −10 s / +30 s (movies and series) |
| C | Channel list |
| M | Mute |
| A | Aspect ratio |
| Mouse wheel, + / − | Volume |

## Data and privacy

- Settings, accounts, favorites and progress: `%APPDATA%\Umbra`
- Downloaded content cache: `%LOCALAPPDATA%\Umbra\cache`
- Error log: `%APPDATA%\Umbra\app.log`. It does not include passwords or stream links.
- Set the `UMBRA_HOME` environment variable to store data and cache in a folder of your choice (portable use or testing).

## Project structure

- `src/Umbra.Core`: Xtream Codes client (`player_api.php`), M3U reader, XMLTV guide, catch-up and storage.
- `src/Umbra.App`: the WPF interface.
  - `App.xaml`: palette, typography and styles for all controls.
  - `Ui.cs` and `Motion.cs`: adaptive scaling and animations.
  - `MainWindow`: navigation bar, current screen and player layer.
  - `Player/PlayerView`: the player (LibVLCSharp), embedded in the main window.
  - `Views/`: Home, Live, Movies/Series, detail pages, Search, Account, Settings and sign-in.

## Self-test

Development self-test (off-screen window, no sound, does not use your saved accounts): walks through every screen, plays a channel and a movie, and leaves screenshots and `report.txt` in the given folder. The last argument is the window size to test.

```bash
publish/Umbra.exe --selftest-xtream http://server:port username password C:\output 1600x900
```

---

# Español

[English](#umbra--iptv-for-windows) · **Español**

Reproductor IPTV nativo para Windows (WPF + VLC). Entras con tus datos de **Xtream Codes** (servidor, usuario y contraseña) o con tu **lista M3U**.

> Umbra es solo un reproductor. No ofrece canales ni contenido; necesitas tu propia suscripción IPTV o lista.

## Índice

- [Requisitos](#requisitos)
- [Instalación](#instalación)
- [Uso](#uso)
- [Interfaz](#interfaz)
- [Funciones](#funciones)
- [Atajos](#atajos)
- [Datos y privacidad](#datos-y-privacidad)
- [Estructura del proyecto](#estructura-del-proyecto)
- [Autoprueba](#autoprueba)

## Requisitos

- Windows 10 u 11, 64 bits
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (para compilar desde el código)
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) (solo para ejecutar una versión publicada)
- Git

LibVLC se instala solo mediante NuGet; **no** hace falta tener VLC instalado.

## Instalación

### Opción A: compilar desde el código

1. Clona el repositorio:

   ```bash
   git clone https://github.com/Nogui23/Umbra.git
   cd Umbra
   ```

2. Comprueba que tienes .NET 9:

   ```bash
   dotnet --version
   ```

3. Restaura las dependencias y compila:

   ```bash
   dotnet build Umbra.sln -c Release
   ```

4. Ejecuta la app:

   ```bash
   dotnet run --project src/Umbra.App/Umbra.App.csproj -c Release
   ```

### Opción B: publicar una carpeta independiente

```bash
dotnet publish src/Umbra.App/Umbra.App.csproj -c Release -o publish
```

Después abre `publish\Umbra.exe`. Puedes copiar la carpeta `publish` a donde quieras (necesita el .NET 9 Desktop Runtime).

### Opción C: Visual Studio

Abre `Umbra.sln` en Visual Studio 2022 (17.12 o posterior) con la carga de trabajo **Desarrollo de escritorio con .NET**, marca `Umbra.App` como proyecto de inicio y pulsa `F5`.

## Uso

1. **Añadir cuenta**:
   - **Xtream Codes**: URL del servidor (`http://servidor:puerto`), usuario y contraseña.
   - **Lista M3U**: una URL o un archivo `.m3u`. La guía EPG en formato XMLTV es opcional.
   - Si pegas un enlace `get.php?username=…&password=…`, se usa la API Xtream completa: fichas de películas, series, guía y catch-up.
2. La app recuerda tus cuentas. Las contraseñas se guardan cifradas con la protección de datos de Windows (DPAPI), solo para tu usuario. La próxima vez entra directamente a la última cuenta que usaste.

## Interfaz

- **Una sola ventana**: el reproductor se abre dentro de la app, encima de la pantalla en la que estabas, y al cerrarlo vuelves a ella.
- **Pantalla completa al abrir**. `F11` cambia entre pantalla completa y ventana (se puede desactivar en Ajustes).
- **Diseño monocromo**: negro, blanco y grises; el único color es el de las carátulas y los logos.
- **Español o inglés**: se elige en Ajustes → Pantalla → Idioma (o en la pantalla de acceso) y cambia al momento, sin reiniciar. Los nombres de canales, películas y categorías son los de tu proveedor.
- **Adaptable**: la interfaz se escala sola a la resolución (de 1024×768 a 4K) y en ventanas estrechas compacta las columnas. En Ajustes puedes hacerla más grande o más pequeña.
- **Animaciones**: transiciones entre pantallas, selector de navegación deslizante, desplazamiento suave y carátulas que aparecen con fundido. Si Windows tiene las animaciones desactivadas, la app tampoco las usa.

## Funciones

- **Inicio**: accesos a TV, películas y series, lo último que has visto, tus favoritos y las novedades.
- **TV en directo**:
  - Categorías, búsqueda, favoritos y canales vistos recientemente.
  - Guía de programación de cada canal y **catch-up** (ver programas ya emitidos) en los canales que lo permiten.
- **Películas y series**:
  - Carátulas, fichas (sinopsis, reparto, duración, nota y tráiler), favoritas y «Seguir viendo».
  - Temporadas y episodios. Se marca lo que ya has visto y el siguiente episodio se reproduce solo.
- **Búsqueda global** en canales, películas y series (`Ctrl+F`).
- **Reproductor VLC**: pistas de audio, subtítulos (también desde archivo), formato de imagen, volumen, lista de canales para hacer zapping y, en directo, el progreso del programa en emisión.
- **Control parental**: haz clic derecho sobre una categoría para bloquearla con PIN (el PIN inicial es `0000`).
- **Cuenta**: estado, fecha de caducidad, conexiones y formatos permitidos. Desde aquí también se actualiza el contenido.
- **Ajustes**: pantalla completa, tamaño de la interfaz, formato de los canales (TS o HLS), búfer de red, aceleración por GPU, ajuste horario de la guía y User-Agent.

## Atajos

| Tecla | Acción |
|---|---|
| F11 | Pantalla completa / ventana |
| Esc | Volver (cierra el reproductor o la ficha abierta) |
| Ctrl+F | Buscar |
| Espacio | Pausa / continuar |
| F o doble clic en el vídeo | Pantalla completa / ventana |
| ↑ ↓ (TV) o Re Pág / Av Pág | Canal anterior / siguiente |
| ← → | −10 s / +30 s (películas y series) |
| C | Lista de canales |
| M | Silenciar |
| A | Formato de imagen |
| Rueda del ratón, + / − | Volumen |

## Datos y privacidad

- Ajustes, cuentas, favoritos y progreso: `%APPDATA%\Umbra`
- Caché del contenido descargado: `%LOCALAPPDATA%\Umbra\cache`
- Registro de errores: `%APPDATA%\Umbra\app.log`. No incluye contraseñas ni enlaces de los streams.
- Con la variable de entorno `UMBRA_HOME` la app guarda datos y caché en la carpeta que indiques (uso portátil o pruebas).

## Estructura del proyecto

- `src/Umbra.Core`: cliente Xtream Codes (`player_api.php`), lector de listas M3U, guía XMLTV, catch-up y almacenamiento.
- `src/Umbra.App`: la interfaz WPF.
  - `App.xaml`: paleta, tipografía y estilos de todos los controles.
  - `Ui.cs` y `Motion.cs`: escala adaptable y animaciones.
  - `MainWindow`: barra de navegación, pantalla actual y capa del reproductor.
  - `Player/PlayerView`: el reproductor (LibVLCSharp), integrado en la ventana principal.
  - `Views/`: Inicio, Directo, Películas/Series, fichas, Buscar, Cuenta, Ajustes y acceso.

## Autoprueba

Autoprueba para desarrollo (ventana fuera de pantalla, sin sonido, no usa tus cuentas guardadas): recorre todas las pantallas, reproduce un canal y una película, y deja capturas y `report.txt` en la carpeta indicada. El último argumento es el tamaño de ventana que se quiere probar.

```bash
publish/Umbra.exe --selftest-xtream http://servidor:puerto usuario clave C:\salida 1600x900
```
