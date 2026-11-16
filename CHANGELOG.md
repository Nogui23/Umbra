# Registro de versiones

Todos los cambios de Umbra, de la versión más nueva a la más antigua.

Las versiones siguen [versionado semántico](https://semver.org/lang/es/): `mayor.menor.parche`.
Sube el **parche** para correcciones, la **menor** para funciones nuevas y la **mayor** cuando algo deja de funcionar como antes (por ejemplo, datos guardados que no se pueden leer).

El número está en [Directory.Build.props](Directory.Build.props) y aparece en Ajustes, al final de la página.

## [Sin publicar]

_Nada todavía._

## [1.1.0] - 2026-11-16

### Nuevo

- **Descargas** de películas y episodios para verlos sin conexión:
  - Botón «Descargar» en la ficha de cada película y en cada episodio, y «Descargar temporada».
  - Pantalla Descargas con cola, pausa, reanudar y eliminar; las descargas cortadas siguen desde donde se quedaron.
  - Tiempo estimado restante y velocidad.
  - Ajustes: carpeta de descargas, descargas a la vez (1 a 3) y, opcional, reconectar cuando el servidor frena la descarga.
  - Si la cuenta no tiene conexiones libres, las descargas se pausan mientras ves un vídeo.
  - Lo descargado se reproduce desde el disco, sin usar la conexión.
- **Biblioteca** con todo lo descargado:
  - Películas y series con su ficha (sinopsis, reparto, carátula y fondo) guardada en el disco.
  - Se abre sin entrar en ninguna cuenta: es la primera pantalla cuando no hay ninguna abierta.
  - **Exportar** lo que elijas a un `.zip` sin ningún dato de tu cuenta, e **importar** ese `.zip` en otro equipo.
  - Ajustes desde la propia biblioteca, mostrando solo los que le afectan cuando no hay listas añadidas.
- **Nuevas fuentes de contenido**:
  - **Jellyfin y Emby** (servidor, usuario y contraseña).
  - **Plex**, con usuario y contraseña (se guarda solo el token) o directamente con el token.
  - **Carpeta o NAS**: los vídeos de una carpeta del equipo o compartida; los archivos `Serie S01E02` se agrupan como series.
- **Conexiones libres** de la cuenta en la pantalla de inicio.
- **Limpiar «Seguir viendo»**: quitar una película (clic derecho) o vaciarlo entero, y «Quitar de «Seguir viendo»» en la ficha.

### Corregido

- Las descargas fallaban con «error 302» cuando el proveedor redirige el archivo de https a http.
- La carátula de una descarga en curso se recargaba sin parar.
- El mensaje de biblioteca vacía salía pegado a la izquierda.

## [1.0.0] - 2026-11-14

Primera versión.

- Cuentas **Xtream Codes** y listas **M3U** (con guía XMLTV opcional), guardadas cifradas con DPAPI.
- **TV en directo** con categorías, favoritos, guía de programación y catch-up.
- **Películas y series** con fichas, temporadas, «Seguir viendo» y siguiente episodio automático.
- Búsqueda global, control parental con PIN, estado de la cuenta y ajustes.
- Reproductor VLC integrado en la ventana, interfaz adaptable, en español e inglés.

[Sin publicar]: https://github.com/Nogui23/Umbra/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/Nogui23/Umbra/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/Nogui23/Umbra/releases/tag/v1.0.0
