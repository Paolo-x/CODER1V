# GTA Codigo OCR

Aplicacion minima para Windows 10/11 x64. F8 activa una seleccion de pantalla, el OCR integrado de Windows lee la imagen **en RAM**, reconoce una secuencia `XXXX-XXXX-XXXX` y la copia al portapapeles para pegarla con `Ctrl+V`.

## Para usarla

1. Ejecuta `GTACodigoOCR.exe`.
2. Pulsa `F8` y arrastra el mouse para seleccionar solamente el codigo mostrado en el stream.
3. Al terminar el reconocimiento, suena la notificacion del sistema y el codigo queda copiado al portapapeles.
4. Cambia al navegador y pulsa `Ctrl+V`.

`Esc` cancela la seleccion. Para cerrar la aplicacion, usa el menu `Salir` de su icono en la bandeja de Windows, junto al reloj.

## Para generar el EXE (una unica vez, en PC de desarrollo)

Instala el **SDK de .NET 10** desde https://dotnet.microsoft.com/download/dotnet/10.0 y ejecuta `compilar.cmd` en Windows 10/11. Tambien puedes abrir un terminal de PowerShell en esta carpeta y ejecutar:

```powershell
dotnet publish .\GTACodigoOCR.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false -o .\dist
```

Copia solamente `dist\GTACodigoOCR.exe` al PC de destino. No requiere instalar .NET, Python ni Tesseract en el destino.

**Nota:** el PC de desarrollo necesita Internet para restaurar paquetes oficiales de Microsoft la primera vez, pero el EXE terminado funciona sin Internet. Los archivos nativos del runtime de .NET pueden extraerse en `%TEMP%\.net` durante el inicio (no son capturas de pantalla). Windows requiere tener instalado al menos un idioma con OCR compatible; la aplicacion intenta ingles estadounidense y luego los idiomas de perfil.

## Alcance

- No sube ni guarda capturas, ni crea historial, ni valida el codigo contra PlayStation.
- Reconoce el **formato**, no si el codigo ha sido emitido o sigue disponible.
- La precision depende de calidad, tamaño y contraste del codigo en la transmision. Si no lo detecta, selecciona el codigo con un margen pequeño y repite F8.
- En equipos con varias pantallas y diferentes escalas de DPI, es importante probar la seleccion en cada monitor.
- La aplicacion no automatiza el canje; solo copia texto para pegado manual.

## Crear el EXE sin instalar nada en el PC (GitHub Actions)

Esta es la opcion recomendada si NO quieres descargar ni instalar el SDK de .NET.
Solo requiere iniciar sesion en GitHub desde el navegador y acceso a Internet **para generar/descargar** el EXE; la app terminada no necesitara Internet para OCR.

1. Abre https://github.com/new y crea un repositorio nuevo (preferentemente **privado**) llamado `GTACodigoOCR`. Elige la rama `main` (predeterminada).
2. Extrae el ZIP y sube al repositorio los archivos `Program.cs`, `GTACodigoOCR.csproj` y la carpeta **`.github`** que contiene `workflows/crear-exe.yml` (tambien puedes incluir README y compilar.cmd). NO subas solo el ZIP.
3. La subida a `main` inicia automaticamente la accion de compilacion. Entra a la pestana **Actions**, selecciona **Crear EXE portable de Windows** y abre la ejecucion mas reciente. Si no se inicio, pulsa **Run workflow**.
4. Debe aparecer una marca verde si la compilacion tuvo exito. En **Artifacts** pulsa **GTACodigoOCR-Windows** y descarga el ZIP generado automaticamente.
5. Extrae el ZIP: dentro estara `GTACodigoOCR.exe`. **Ese unico EXE** es lo que usaras; no necesitas instalar el SDK ni los archivos del proyecto en tu PC.

Si la accion sale roja, abre el registro de error en **Crear EXE independiente** y comparte el mensaje: este proyecto no ha sido compilado en un Windows real desde este entorno.
