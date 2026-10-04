# GS-232b-Interface


A small Windows app that points a Yaesu GS-232B controlled az/el rotator at amateur radio satellites.

- The top half of the window shows a map in Mercator or a planar view centred on your station. It includes the ground track, coverage circle and day/night shading.
- The bottom half has an azimuth gauge and an elevation gauge. Each shows where the rotator is, where it's being sent and where the satellite is.
- It is built to stay readable at a quarter of a 1080p screen, with an always-on-top option.

## Build and run

You need Windows 10 or 11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
dotnet run --project src/SatTrack.App
```

Or open `SatTrack.sln` in Visual Studio 2022 and press F5.

To make a single `SatTrack.exe` you can copy to another PC, run this. The exe is large because it bundles the .NET runtime, but it needs nothing else installed:

```
dotnet publish src/SatTrack.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

Use `--self-contained false` for a small exe on PCs that already have the .NET 8 Desktop Runtime.

## First run

1. **Settings** opens automatically.
   - Enter your location as a grid square (`FM19la`) or as latitude, longitude (`39.29, -76.61` or `39.29N 76.61W`).
   - Pick the COM port and baud rate of the GS-232B. 9600 is the factory default.
   - Set the travel of your rotator: azimuth 0–360° or 0–450°, and elevation 0–90° or 0–180°.
2. Orbital data downloads in the background. The status bar shows how old the elements are.
3. Pick a satellite from the list at the top left.

## Using it

| Control | What it does |
|---|---|
| **Connect** | Opens the COM port, asks the controller for its position (`C2`) and shows it on the gauges. If the controller doesn't answer you get a message saying why. |
| **Enable** | Arms the app. It sends no movement commands until you press this. When armed, the button turns red and reads **Disarm**. |
| **Disarm** / **Esc** | Stops sending commands and sends `S` (all stop) immediately. Esc works from anywhere in the window. Disconnecting or closing the app also disarms. |
| **Track** | Follows the selected satellite. If you're not armed, the gauges still show the target, so you can preview a pass. Changing satellites stops tracking. |
| **Simulate** | Swaps in a simulated rotator and clock (see below). |
| **On top** | Keeps the window above other windows. |
| **Menu** | Settings, *Update orbital data* (F5), satellite lists, map and theme options. |
| Map chips | Bottom-right of the map: *Mercator / Planar* and *Dark / Light*. Mouse wheel zooms; double-click resets. |

While tracking, the rotator moves to the rise point a couple of minutes before each pass, follows the satellite, then holds or parks. It aims slightly ahead of the satellite (default 2 s) to make up for rotator lag. It only sends a new position when the target has moved by your threshold (default 2°), so the motors don't chatter.

### Cable wrap and flip

Before each pass the app plans the whole pass. It picks, for every moment, whether to point normally or *flipped* (azimuth + 180°, elevation 180° − el, when the rotator allows 0–180°). It also picks which side of the 360–450° overlap to use. The aim is that the rotator never has to swing through its end stop mid-pass:

- **0–450°:** most passes, including ones through north, are tracked without an unwind.
- **0–180° elevation:** overhead passes are flown over the top instead of with a 180° azimuth swing.
- **0–360° and 0–90°:** some passes through north need an unwind. The planner puts it as close to the horizon as it can, and briefly waits at the end stop instead of unwinding when the satellite is only just past it.

The map's info box shows "flip" when a pass will use it. The azimuth gauge shades the overlap zone. When the rotator is in the overlap, the readout shows the controller's raw value, such as "Rotator (423° on controller)".

### Simulation

Turn on **Simulate**, then **Connect**. This connects a simulated rotator that moves at roughly G-5500 speeds (6°/s azimuth, 2.7°/s elevation) with the same travel limits as your settings.

- **Next pass** jumps the clock to just before the next pass of the selected satellite.
- The speed box runs time at 1×, 5×, 20× or 60×.
- Press **Enable** and **Track** to watch a whole pass, including pre-positioning, flips and parking.

Turning simulation off disconnects the simulated rotator and returns to real time.

## Satellite lists

The built-in list has 96 satellites from AMSAT's daily element distribution, with the popular FM and linear birds first. To customise it:

1. Use **Menu > Save built-in list as…** to get a copy.
2. Edit it.
3. Use **Menu > Load satellite list…**. The app remembers the file.

```json
{
  "description": "My list",
  "satellites": [
    { "name": "ISS",   "noradId": 25544, "notes": "Shown as a tooltip" },
    { "name": "SO-50", "noradId": 27607 },
    {
      "name": "New cubesat",
      "tle1": "1 99999U 26001A   26276.50000000  .00000000  00000-0  00000-0 0  9999",
      "tle2": "2 99999  97.5000 100.0000 0010000  90.0000 270.0000 15.20000000    01"
    }
  ]
}
```

- `noradId` is used to fetch elements online.
- `tle1` and `tle2` are optional. When present they are used instead of downloaded data, which is handy for new launches with no public elements yet.
- Comments and trailing commas are allowed.
- Entries with problems are skipped, and the status bar says why.

## Where the data comes from

- **Orbital elements:** these come from the AMSAT daily file (`https://www.amsat.org/tle/dailytle.txt`, one request for every amateur satellite). Anything missing there is requested from CelesTrak by catalog number, in OMM JSON format, which also handles the 6-digit catalog numbers that no longer fit in a TLE. Downloads are cached, refresh automatically after 12 hours, and the app works offline from the cache.
- **Map:** Natural Earth 1:50m land and borders (public domain), embedded in the exe.
- **Settings and cache:** `%LOCALAPPDATA%\SatTrack\`.

## Troubleshooting the GS-232B

- **"The port opened but the controller didn't report a position":** check the baud rate and the cable, and that the controller is powered. You can test by hand in PuTTY (serial, 9600 8N1): type `C2` and Enter, and you should see `AZ=xxx  EL=xxx`.
- **"The port is in use by another program":** close other rotator software (e.g. PstRotator, SatPC32) or other terminals.
- **Rotator points the wrong way by 180°:** the app expects controller azimuth 0 to be north. If your controller is set up for south-centre operation, switch it back to north-centre.
- **Motion lags the satellite:** increase *Aim ahead of the satellite* in Settings. Speed also affects this; the GS-232B `X1`–`X4` speed commands are available in `Gs232bRotator` if you want to add a control for them.

## Project layout

```
SatTrack.sln
src/SatTrack.Core/        tracking logic, no UI (net8.0)
  Catalog/                satellite list model + built-in DefaultSatellites.json
  Geo/                    grid squares, station location, spherical maths
  Orbit/                  element download/cache, SGP4 predictor, pass finder
  Rotator/                IRotator, GS-232B serial driver, simulated rotator
  Tracking/               pass planner (wrap/flip), tracking engine
  Time/                   real and simulation clocks
  Settings/               settings model and JSON store
src/SatTrack.App/         WinForms UI (net8.0-windows)
  Controls/               map view + projections, azimuth/elevation gauges
third_party/SGP.NET/      SGP4 library, MIT licence (vendored 1.6.0 source)
```

The tracking engine runs on a background thread and publishes an immutable snapshot that the UI reads ten times a second. All rotator I/O goes through one gate. A disarm is therefore always processed after any move command that's already in flight, and no new move can be sent once it has been processed.

## Licences

- SatTrack source: yours to use as you like.
- SGP.NET: MIT, © 2019 Colby Newman (see `third_party/SGP.NET/LICENSE`).
- Natural Earth data: public domain.
