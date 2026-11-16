# Registro de versiones

Todos los cambios de Umbra, de la versión más nueva a la más antigua.

Las versiones siguen [versionado semántico](https://semver.org/lang/es/): `mayor.menor.parche`.
Sube el **parche** para correcciones, la **menor** para funciones nuevas y la **mayor** cuando algo deja de funcionar como antes (por ejemplo, datos guardados que no se pueden leer).

El número está en [Directory.Build.props](Directory.Build.props) y aparece en Ajustes, al final de la página.

## [Sin publicar]

### Nuevo

- **Perfiles**, comunes a todas las listas y a la Biblioteca: cada persona tiene sus favoritos, su progreso, su «Seguir viendo» y su audio y subtítulos preferidos, sin mezclarse con los demás.
  - Con más de un perfil, la app empieza con «¿Quién está viendo?».
  - Arriba a la derecha se ve el perfil en uso; al pulsarlo se cambia de perfil o se gestionan.
  - Cada perfil puede tener una **foto propia** (JPG o PNG, recortada en círculo) o su inicial sobre un color.
  - Las categorías bloqueadas se comparten entre los perfiles de una lista, para que el control parental no se salte cambiando de perfil.
  - Lo que había antes pasa al primer perfil, «Principal», sin perder nada. La copia de seguridad guarda todos los perfiles con sus fotos.

- **Audio y subtítulos preferidos**: el reproductor recuerda el idioma de audio y de subtítulos (o subtítulos desactivados) que eliges y lo aplica solo en los siguientes vídeos. Se ve y se olvida en Ajustes → Reproducción.
- **Series en «Seguir viendo»**: aparecen junto a las películas con el episodio por el que vas («T2 E5»); al acabar un episodio pasa al siguiente y la serie sale al terminar el último. Lo más reciente va primero.
- **Cambiar de canal escribiendo el número** en directo (teclado o teclado numérico): salta tras una pausa corta o con Intro, también a canales de otra categoría.
- **Velocidad de reproducción** de 0,5× a 2× en películas y series; se mantiene en los siguientes vídeos mientras el reproductor sigue abierto.
- **Temporizador de apagado**: 15 a 120 minutos o «al terminar este episodio»; baja el volumen poco a poco y pausa (en directo, detiene el canal para liberar la conexión).
- **Aviso de versiones nuevas**: una vez al día se consulta a GitHub la última versión publicada (se puede desactivar). Ajustes → Actualizaciones muestra la versión y abre su página; no descarga ni instala nada.
- **Copia de seguridad** de favoritos, progreso, «Seguir viendo» y ajustes (Ajustes → Avanzado). Sin contraseñas, tokens, enlaces de listas ni el PIN; al importarla se une con lo que ya hay.
- **Aviso de Windows** cuando termina una descarga con la app minimizada o detrás de otras ventanas (se puede desactivar).

### Seguridad

- Las descargas y los `.zip` importados solo se guardan con extensiones de vídeo: un proveedor o un `.zip` no pueden dejar un programa (`.exe`, `.lnk`…) en la carpeta de descargas.
- Importar un `.zip` de biblioteca comprueba que cada archivo no ocupe más de lo que declara (archivos «bomba») y que las carátulas sean imágenes de verdad.
- Las carátulas de una descarga solo se copian del disco cuando el vídeo viene de una carpeta o NAS; un proveedor en línea no puede hacer que se copie (y luego se exporte) un archivo privado del equipo.
- Una copia de seguridad ya no puede cambiar la página que abre «Ver novedades», ni crear cuentas de carpeta con rutas de dispositivo, ni traer fotos que no sean PNG; los ajustes fuera de rango se corrigen y los archivos de más de 64 MB se rechazan.
- El tráiler de una película solo abre páginas web (http/https).
- Los nombres reservados de Windows (`CON`, `NUL`, `COM1`…) ya no rompen una descarga.

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
