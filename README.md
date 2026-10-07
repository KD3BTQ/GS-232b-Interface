# GS-232B Interface Software

A small Windows app that points a Yaesu GS-232B controlled az/el rotator at amateur radio satellites and keeps a FlexRadio (or ICOM) transceiver on the Doppler-corrected frequency.

- The top half of the window shows a map in Mercator or a planar view centred on your station. It includes the ground track, coverage circle and day/night shading.
- The bottom half has an azimuth gauge and an elevation gauge. Each shows where the rotator is, where it's being sent and where the satellite is.
- It is built to stay readable at a quarter of a 1080p screen, with an always-on-top option.

## Build and run

You need Windows 10 or 11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
dotnet run --project src/SatTrack.App
```

Or open `SatTrack.sln` in Visual Studio 2022 and press F5.

To make a single `GS232B-Interface.exe` you can copy to another PC, run this. The exe is large because it bundles the .NET runtime, but it needs nothing else installed:

```
dotnet publish src/SatTrack.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

Use `--self-contained false` for a small exe on PCs that already have the .NET 8 Desktop Runtime.

To build an installer, publish as above, then open `installer/GS232B-Interface.iss` in [Inno Setup](https://jrsoftware.org/isdl.php) 6.3+ and press Compile. The setup exe appears in `installer-output/`.

(The code folders and solution are still named `SatTrack`; that's internal only.)

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
| **Upcoming passes** | Opens a window listing every pass of every satellite in the list (see below). Also Ctrl+P. |
| **On top** | Keeps the window above other windows. |
| **Menu** | Settings, *Update orbital data* (F5), *Manual slew* (Ctrl+M), *Simulation mode*, *Rotator serial debug* (Ctrl+D), *Radio serial debug* (Ctrl+R), satellite lists, *Show frequency panel*, map and theme options. |
| Map chips | Bottom-right of the map: *Mercator / Planar* and *Dark / Light*. Mouse wheel zooms; double-click resets. |

While tracking, the rotator moves to the rise point a couple of minutes before each pass, follows the satellite, then holds or parks. It aims slightly ahead of the satellite (default 2 s) to make up for rotator lag. It only sends a new position when the target has moved by your threshold (default 2°), so the motors don't chatter.

### Upcoming passes

**Upcoming passes** (toolbar, or Ctrl+P) opens a window covering every satellite in the current satellite list.

- Enter **Passes in the next** N **hours** or **days** (up to 30 days), and optionally **Highest point at least** N° to hide low passes. Press **Calculate** (or F5). The search runs in the background with a progress bar; the full built-in list over a week takes a few seconds.
- The **table** lists each pass: satellite, rise time, highest elevation and when it happens, set time, duration, and rise and set azimuth. Click a column header to sort. Tick **Local time** to show times in your time zone instead of UTC. Geostationary satellites that never set from your location show as "always up".
- The **map** shows the ground track of every pass in the table (Mercator or Planar, using the chips in its corner). Select rows to highlight their tracks; each highlighted track gets a dot and a label where the pass starts. When a search returns more than 600 passes, only the selected ones are drawn, to keep the map readable.
- **Double-click** a row to select that satellite in the main window.
- In simulation mode, "now" is the simulated time.

### Frequency control (FlexRadio or ICOM)

The top half of the window is split: the map on the left and the frequency panel on the right. Turn the panel off with **Menu > Show frequency panel** to get the full-width map back.

1. Enter the satellite's nominal **Downlink MHz** (what it transmits). If it's a repeater, enter the **Offset MHz** (uplink minus downlink, e.g. -291.81 for an FM repeater with a 437.800 downlink and a 145.990 uplink). Press Enter or click away to apply. The app remembers the values for each satellite.
2. The panel shows the Doppler shift now (and how fast it's changing), the corrected **Receive** frequency, the corrected **Transmit** frequency if there's an offset, the frequency the **Radio** reports, and a chart of the Doppler shift across the pass, with the time the satellite is above the horizon shaded.
3. The radio buttons work like the rotator's: **Connect** opens the radio's port and reads its frequency, **Enable** allows frequency commands (it turns red and reads **Disarm**), **Track** keeps the radio on the corrected frequency. Esc disarms both the rotator and the radio.

Details:

- In **Settings > Radio**, choose the radio type:
  - **FlexRadio (SmartSDR CAT port):** in the SmartSDR CAT app, create a CAT port (any COM number) and pick that port here. The app reads and sets slice A with the standard CAT commands `FA;` and `FA00014074000;` (frequency in Hz, 11 digits). Baud rate doesn't matter for SmartSDR's virtual ports. SmartSDR CAT must be running and connected to the radio.
  - **ICOM (CI-V):** choose the COM port, set the baud rate to match the radio's CI-V menu, and enter its CI-V address in hex (for example 7C for an IC-9100, 74 for an IC-7700).
- By default the radio is tuned to the corrected downlink (receive). Settings can switch it to the corrected uplink instead.
- The radio is retuned whenever the corrected frequency moves by the retune step (default 10 Hz), at most five times a second. Turning the VFO knob while tracking is overridden on the next update; press **Track** again to stop.
- The app keeps DTR and RTS low on the radio port, because SmartSDR CAT and many ICOM USB interfaces can use those lines for PTT or CW keying.
- The radio's frequency is polled once a second (shown in the panel). Three failed polls disarm the radio and show "Radio connection lost".
- **Frequency coverage:** most FlexRadio models (6400/6600/8000 series) tune up to 54 MHz; the FLEX-6700 also covers 135–165 MHz. A frequency the radio can't tune is rejected (`?;` from a Flex, NG from an ICOM) and the panel reports that the command failed. For 145/435 MHz satellites use a radio that covers them, or a transverter (enter the IF-side frequency).
- The offset is a fixed difference between uplink and downlink, which is right for FM repeaters and for a single point in a non-inverting transponder passband. Inverting linear transponders need different arithmetic and aren't handled yet.
- In simulation mode, **Connect** in the panel uses a simulated radio of the selected type, and the radio debug window shows the CAT commands or CI-V frames that would be sent.

**Menu > Radio serial debug** (Ctrl+R) shows everything sent to and received from the radio: CAT commands as text (for example `FA00029400674; [set frequency 29.400674 MHz]`), or ICOM CI-V frames in hex with a description. For ICOM, echoes of the app's own frames, OK/NG replies and unsolicited broadcasts are labelled. Frequency polls count as routine and can be hidden. **Save log…** works the same as for the rotator.

### Manual slew

**Menu > Manual slew** (Ctrl+M) opens a small window where you type an azimuth and elevation and press **Slew** (or Enter).

- Values are in rotator coordinates: in 0–450° mode, azimuths over 360 use the overlap; in 0–180° elevation mode, over 90 means flipped.
- The rotator must be connected and enabled. Slewing stops tracking.
- **Stop** halts motion but stays enabled. **Use current** fills in where the rotator is now. **Park** goes to the park position from Settings.
- Esc disarms from this window too.
- The window stays open beside the main one, and the gauges show the commanded position as the target.

### Cable wrap and flip

Before each pass the app plans the whole pass. It picks, for every moment, whether to point normally or *flipped* (azimuth + 180°, elevation 180° − el, when the rotator allows 0–180°). It also picks which side of the 360–450° overlap to use. The aim is that the rotator never has to swing through its end stop mid-pass:

- **0–450°:** most passes, including ones through north, are tracked without an unwind.
- **0–180° elevation:** overhead passes are flown over the top instead of with a 180° azimuth swing.
- **0–360° and 0–90°:** some passes through north need an unwind. The planner puts it as close to the horizon as it can, and briefly waits at the end stop instead of unwinding when the satellite is only just past it.

The map's info box shows "flip" when a pass will use it. The azimuth gauge shades the overlap zone. When the rotator is in the overlap, the readout shows the controller's raw value, such as "Rotator (423° on controller)".

### Simulation

Turn on **Menu > Simulation mode**, then **Connect**. While it's on, a *Simulation* label, a **Next pass** button and a speed box appear on the toolbar. This connects a simulated rotator that moves at roughly G-5500 speeds (6°/s azimuth, 2.7°/s elevation) with the same travel limits as your settings.

- **Next pass** jumps the clock to just before the next pass of the selected satellite.
- The speed box runs time at 1×, 5×, 20× or 60×.
- Press **Enable** and **Track** to watch a whole pass, including pre-positioning, flips and parking.

Turning **Simulation mode** off disconnects the simulated rotator and returns to real time.

## Serial debug window

**Menu > Serial debug window** (Ctrl+D) shows every command sent to the controller (TX) and every reply (RX), with millisecond UTC timestamps. It also shows connection events, errors and the app's status messages.

- Carriage returns and line feeds are shown as `\r` and `\n`, so you can see exactly what the controller sends back, including echoes and `?>` errors.
- *Hide position polls* filters out the once-a-second `C2` / `AZ=… EL=…` exchange so the moves and errors stand out.
- **Save log…** writes the whole session (not just what's on screen) to a `.log` text file. **Copy selected** and Ctrl+C copy lines to the clipboard.
- The log starts when the app opens, so you can open the window after a problem and still see what happened. It keeps the most recent million lines, about a week of continuous polling.
- In simulation mode the window shows the commands a real GS-232B would have received.

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
    { "name": "SO-50", "noradId": 27607, "downlinkMHz": 436.795, "offsetMHz": -290.945 },
    {
      "name": "New cubesat",
      "tle1": "1 99999U 26001A   26276.50000000  .00000000  00000-0  00000-0 0  9999",
      "tle2": "2 99999  97.5000 100.0000 0010000  90.0000 270.0000 15.20000000    01"
    }
  ]
}
```

- `noradId` is used to fetch elements online.
- `downlinkMHz` and `offsetMHz` are optional; they pre-fill the frequency panel the first time you pick that satellite.
- `tle1` and `tle2` are optional. When present they are used instead of downloaded data, which is handy for new launches with no public elements yet.
- Comments and trailing commas are allowed.
- Entries with problems are skipped, and the status bar says why.

## Where the data comes from

- **Orbital elements:** these come from the AMSAT daily file (`https://www.amsat.org/tle/dailytle.txt`, one request for every amateur satellite). Anything missing there is requested from CelesTrak by catalog number, in OMM JSON format, which also handles the 6-digit catalog numbers that no longer fit in a TLE. Downloads are cached, refresh automatically after 12 hours, and the app works offline from the cache.
- **Map:** Natural Earth 1:50m land and borders (public domain), embedded in the exe.
- **Settings and cache:** `%LOCALAPPDATA%\GS-232B Interface Software\`.

## Troubleshooting the radio

- **"The radio didn't answer" (Flex):** check that SmartSDR CAT is running and connected to the radio, and that the COM port you picked is a CAT port (not a PTT/CW-only port). Open the radio serial debug window: you should see `FA;` going out and `FA…;` coming back.
- **"The radio didn't answer" (ICOM):** check the CI-V address and that the baud rate matches the radio's CI-V setting. In the debug window, if you see only your own frames echoed back, the radio isn't answering; if you see nothing at all, check the cable and port.
- **"The radio rejected the command":** the frequency is outside what the radio can tune (see coverage above).
- **Radio jumps back after you turn the knob:** that's tracking doing its job. Stop tracking to tune by hand.

## Troubleshooting the GS-232B

- **Anything odd:** open the serial debug window and look at the raw TX/RX lines; save the log if you want to share it.
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
  Orbit/                  element download/cache, SGP4 predictor, pass finder, multi-satellite pass search
  Rotator/                IRotator, GS-232B serial driver, simulated rotator, comm log
  Radio/                  FlexRadio CAT and ICOM CI-V drivers, simulated radio, Doppler maths, radio controller
  Tracking/               pass planner (wrap/flip), tracking engine
  Time/                   real and simulation clocks
  Settings/               settings model and JSON store
src/SatTrack.App/         WinForms UI (net8.0-windows)
  Controls/               map view + projections, azimuth/elevation gauges
third_party/SGP.NET/      SGP4 library, MIT licence (vendored 1.6.0 source)
```

The tracking engine runs on a background thread and publishes an immutable snapshot that the UI reads ten times a second. All rotator I/O goes through one gate. A disarm is therefore always processed after any move command that's already in flight, and no new move can be sent once it has been processed.

## Licences

- This software's source: yours to use as you like.
- SGP.NET: MIT, © 2019 Colby Newman (see `third_party/SGP.NET/LICENSE`).
- Natural Earth data: public domain.
