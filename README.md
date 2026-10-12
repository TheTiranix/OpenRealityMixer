# OpenRealityMixer

Alternativa libre a *Reality Mixer* (Steam): muestra el mundo real dentro de VR mediante "cajas de realidad" flotantes
que se activan por proximidad, mirada, boton o gesto de mano. Es un overlay de SteamVR escrito en C# / .NET 10
(WPF + Direct3D 11 + OpenVR).

## Como se conecta (no hay que conectar nada a mano)

La app **solo habla con SteamVR**. No se conecta al visor ni a ALVR directamente:

```
Quest 3S  <-->  ALVR o Steam Link  <-->  SteamVR  <-->  OpenRealityMixer
```

1. Conecta el visor como siempre (ALVR o Steam Link) y abre SteamVR.
2. Abre OpenRealityMixer. Detecta SteamVR y se engancha sola; la cabecera pasa de naranja a verde ("Conectado a SteamVR").
3. Opcional: marca *Iniciar con SteamVR* y arrancara sola cada vez.

Lo unico que si tiene que coincidir entre las dos apps es el **color clave** del passthrough (ver Quest, abajo).

## Tarjeta grafica (integrada / dedicada)

Hay dos cosas que pueden usar la GPU equivocada en un portatil o PC con dos tarjetas, y la app cubre las dos:

- **El motor** (lo que dibuja las cajas): por defecto usa **la GPU a la que SteamVR conecta el visor** (la identifica por LUID).
  Es la opcion recomendada, porque SteamVR solo puede mostrar overlays creados en esa misma GPU. En la cabecera, *Tarjeta grafica*
  permite elegir otra; al cambiarla se reinicia la sesion de SteamVR de la app. Si eliges una distinta de la de SteamVR,
  el estado lo avisa.
- **La ventana de configuracion**: Windows suele dibujarla en la integrada. Marca *Ventana en la GPU de alto rendimiento (Windows)*
  (equivale a *Configuracion > Pantalla > Grafico > Alto rendimiento* para esta app) y reinicia la app.

## Compilar y ejecutar

Requisitos: Windows 10/11 x64, SDK de .NET 10, SteamVR.

```
dotnet build -c Release
bin\Release\net10.0-windows10.0.19041.0\OpenRealityMixer.exe
```

Autoprueba sin visor (shader, proyeccion, chroma key, activacion, colocacion, manos; escribe PNGs de ejemplo):

```
OpenRealityMixer.exe --selftest .\selftest-out
```

La configuracion se guarda en `%AppData%\OpenRealityMixer\settings.json` y el registro en `log.txt` de la misma carpeta.

## Tres modos de imagen

| Modo | Que hace | Visores |
|------|----------|---------|
| **Ventana a la realidad** | Lee la camara del visor con `IVRTrackedCamera` y proyecta cada pixel de la caja sobre la imagen, de modo que la caja es una ventana alineada con lo que hay detras. Admite chroma key, ajustes de imagen y mascara. | Valve Index, WMR (HP G2...), Vive. En SteamVR: *Ajustes > Camara > Activar camara*. |
| **Recorte plano** | Pega un recorte de la imagen de una webcam USB. | Cualquiera, con webcam. |
| **Passthrough por color** | No usa la camara. Pinta la forma de la caja con un color solido y la app de streaming del visor sustituye ese color por el passthrough real. | **Quest 2/3/3S, Pico**, con **ALVR** (chroma key) o Virtual Desktop. |

### Quest 3S: por que hace falta el modo por color y ALVR

Meta no entrega los pixeles de las camaras de la Quest a ninguna app del PC, y Steam Link, Air Link y Quest Link no
publican una camara en `IVRTrackedCamera`. Por eso en Quest no hay chroma key sobre la imagen real ni ventana alineada.
Lo que si se puede es decirle al visor *donde* quieres ver la realidad: la caja se pinta de un color y la app de
streaming lo convierte en una ventana al passthrough nativo (sin latencia).

De las opciones, **ALVR (v20.13 o mas nueva) trae chroma key de passthrough** (modos RGB y HSV); Steam Link no tiene un
modo equivalente que yo haya podido encontrar.

1. En OpenRealityMixer, pon la caja en **Passthrough por color** y elige el *Color clave* (por defecto `#FF00FF`).
2. En ALVR, activa el passthrough en modo **chroma key RGB** y ponle ese mismo color.
3. Ajusta la tolerancia en ALVR hasta que la caja salga limpia, y usa un borde suave bajo (0.02 - 0.08) en la app.

Notas de ALVR: el modo RGB puede dejar un borde del color alrededor de las formas, y en la 20.14 hay un
[fallo conocido](https://github.com/alvr-org/ALVR/issues/2939) con el modo HSV en Quest 3S (solo quita grises); usa RGB.

## Color de las cajas (modo Passthrough por color)

- El **color clave global** (cabecera, "Color clave (Quest)") es el que se pinta en todas las cajas y debe coincidir con el chroma key
  de ALVR. Es un selector real: colores rapidos, codigo hexadecimal (`#FF00FF`, `FF00FF` o `#F0F`) y ajuste fino RGB. **Se aplica al
  instante**, sin tener que salir del campo.
- ALVR trae por defecto verde (R 0, G 255, B 0). Puedes poner ese mismo verde aqui o cambiar ALVR a otro color (por ejemplo magenta:
  R 255, G 0, B 255); lo importante es que sean iguales.
- Cada caja puede tener un **color propio** ("Usar un color propio para esta caja"). Ojo: ALVR solo quita un color a la vez, asi que una
  caja con un color distinto se vera como un panel solido.
- Si en el visor las cajas siguen viendose de color en vez de transparentes, el color de ALVR no coincide o su *Distance threshold* es
  demasiado bajo; la app no puede ver lo que ALVR recibe.

## Formas dibujadas a mano

Cualquier caja puede tener la forma que dibujes tu, no solo rectangulo o elipse:

1. Selecciona la caja y pulsa **Dibujar ahora** (Forma e imagen). Ponte el visor.
2. Aparece un punto de color en la punta de cada mano o mando (cian = izquierdo, amarillo = derecho).
   **Manten el gatillo** (o pellizca con la mano, o manten el boton extra asignado) y recorre el contorno de lo que quieres ver:
   alrededor del teclado, de una puerta, de un vaso. Veras una vista previa del trazo mientras dibujas.
3. **Suelta**: la caja se coloca sobre el trazo, se ajusta a su tamano y toma esa forma. Si dibujas sobre una mesa queda plana contra
   ella; sobre una pared, vertical. Esc cancela.

El contorno se guarda con la caja (se puede mover, escalar con las manos y aplicar borde suave como cualquier otra). *Volver a rectangulo*
la restaura. Mientras dibujas, el gatillo lo recibe la app y no el juego. Si el punto aparece donde no toca, cambia *Punta del lapiz*.

## Manos (hand tracking)

Las manos se leen por la **entrada esqueletica de SteamVR** (SteamVR Input), que es por donde Steam Link (SteamVR 2.8+)
y ALVR (modo de emulacion de manos) las pasan al PC. Hace falta que tu app de streaming tenga el hand tracking activado.

- **Mover cajas**: pellizca (pulgar con indice) dentro de una caja y sigue a tu mano, con su giro. Al soltar se queda ahi y se guarda.
- **Escalar**: con una mano sujetando la caja, pellizca la misma caja con la otra y separa o junta las manos.
- **Anclar a una mano**: ancla *Mano izquierda/derecha* (por ejemplo una "ventana en la muneca"). Si no hay hand tracking usa el mando de ese lado.
- **Gestos como botones**: en una caja con activacion por boton, *Asignar...* y haz el gesto: pellizco, puno, senalar o mano abierta.
- *Colocar rapido > En mi mano* pone la caja donde tienes la mano tras 3 segundos.

La seccion *Manos* de la app muestra en vivo que detecta en cada mano (gesto y distancia pulgar-indice en mm), util para
ajustar la distancia de pellizco.

## Activacion

Siempre visible, **proximidad** (se desvanece al alejarte), **mirada** (con retardo e histeresis) o **boton** (alternar o
mantener). Los botones se leen globalmente: teclado, raton, mandos Xbox (XInput), cualquier joystick de Windows
(volantes, HOTAS, pedales) y gestos de mano, tambien con el juego en primer plano.

## Anclas

Mundo, cabeza, mando izquierdo/derecho, mano izquierda/derecha o un tracker (Vive/Tundra).

## Licencia

[MIT](LICENSE). Los componentes de terceros (OpenVR, CommunityToolkit.Mvvm, Vortice) conservan sus propias licencias,
recogidas en [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). No tiene relacion con *Reality Mixer* de Reality Inside Ltd.;
es una implementacion independiente que no usa su codigo ni sus recursos.

## Estado

- Probado sin visor (`--selftest`, 87 comprobaciones): matematica, shader, chroma key, colores, mascaras, activacion, colocacion,
  gestos, agarre, escalado, anclas de mano, ajuste de trazos a cajas, maquina del dibujo, validez de los archivos de SteamVR
  Input, seleccion de GPU, renderizado en cada tarjeta real del equipo y autoguardado. La interfaz se ha probado con
  automatizacion de UI (cambiar el color, abrir los menus).
- **Sin probar con hardware**: la conexion real con SteamVR, los overlays dentro del visor, la camara de un Index/WMR, la
  lectura real de las manos y el gatillo durante el dibujo (depende de que SteamVR entregue esa entrada a un overlay). Los archivos de bindings cubren Index, Touch, Vive, WMR y HP; si tu emulacion de manos
  aparece como otro tipo de mando, la app mostrara "no detectada" en la seccion *Manos* y lo dira el registro.
- Si la imagen de camara sale invertida, con colores cambiados o desalineada, usa *Calibracion y avanzado*.
- Pendiente: camara estereo (hoy se usa la primera camara, vision plana).
