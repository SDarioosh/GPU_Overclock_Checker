# GPU Overclock Checker

A Windows 11 tool that stress-tests a GPU overclock/undervolt while recording every sensor, then works out **where** it fails and **why**:

- undervolt too deep at peak boost clocks
- voltage transients during load steps
- vdroop under heavy load
- core frequency too high
- memory overclock or Fast Timing past its limit, including silent error-correction retries that lower bandwidth without crashing
- thermal problems such as the RX 7900 XTX hotspot delta

It gives you concrete next steps such as *"raise the voltage offset from -100 mV to -90 mV, or lower Max Frequency from 3050 to 3000 MHz"*.

It is built mainly for the **AMD Radeon RX 7900 XTX**. It works with any Radeon from RDNA1 to RDNA4 and with NVIDIA GeForce cards. It also runs on Intel Arc, but without telemetry.

## Getting it

- **Download:** open the repository's **Actions** tab, pick the latest `build` run and download the `GpuOcChecker-win-x64` artifact. It contains one self-contained `GpuOcChecker.exe` and needs no installation. Pushing a `v*` tag also attaches the exe to a GitHub Release.
- **Build it yourself:** install the [.NET 8 SDK](https://dotnet.microsoft.com/download) and run `.\build.ps1`. The exe is written to `publish\`.

Double-click the exe to get a menu, or use the command line (`GpuOcChecker help`).

## Recommended workflow

1. **Set a baseline.** Reset tuning to default in AMD Software, then run *Save stock baseline* (`GpuOcChecker stress --save-baseline`). Later runs are compared against it. This is how the tool detects memory overclocks that are *slower* than stock and undervolts that cause clock stretching.
2. **Enter your tuning** (menu → *Set my current tuning*, or `--settings "uv=-80,max=2950,min=500,mem=2700,ft=1,pl=15"`). The tool works without it, but then its recommendations are generic.
3. **Run a stress test**: quick (~4 min), standard (~11 min) or thorough (~57 min).
4. **Tune the memory** with `memtune`. Change the VRAM clock in AMD Software, press Enter and let the tool measure. It shows where bandwidth stops rising.
5. **Game with `monitor` running.** If a game crashes the driver, the report shows what the card was doing in the seconds before the crash and what that points to.

If the PC hard-crashes or reboots during a test, just start the tool again. Sessions are journaled to disk as they run, so the next launch diagnoses the crash automatically.

## How it tells the causes apart

The stress test runs phases that load the card in different ways. The cause of a failure is read from **what the card was doing when it failed**:

| Phase | What the card does | A failure here means |
|---|---|---|
| VRAM bandwidth | Streams over buffers far larger than Infinity Cache | Bandwidth lower than baseline, or erratic → memory is retrying errors (clock or Fast Timing too high) |
| VRAM pattern test | Moving-inversion patterns over most of the VRAM | Any error → memory overclock unstable |
| Light load / max boost | A few compute units busy; clocks go to Max Frequency | Undervolt too deep for the top of the V/F curve (the classic RDNA3 undervolt crash), or Max Frequency too high if the voltage is at stock |
| Transient load steps | Bursts of full load with random idle gaps | Voltage can't absorb the load steps → undervolt too deep |
| Heavy sustained load | Whole GPU busy, power-limited, heat-soaked | Vdroop under load (undervolt), or temperature-related if the hotspot is near its limit |

**How failures are detected**

- **Compute verification.** A chaotic floating-point kernel amplifies any single-bit computation error. Its result is checked against a reference on every dispatch. (This is how OCCT-style error checks work.)
- **Driver timeouts (TDR).** The tool reads *Display 4101*, `amdkmdag` / `nvlddmkm` driver errors and WER `LiveKernelEvent 141/117` from the Windows event log. It watches for them live and also reads the log afterwards.
- **Other failures.** OpenCL "device lost" errors, GPU hangs (no work completed for 10 s), telemetry freezes, and abrupt session ends (matched against *Kernel-Power 41* after a reboot).

**Always checked**

- Hotspot temperature and hotspot-minus-edge delta
- Memory junction temperature and VRM temperature
- Power-limit behaviour
- WHEA hardware errors
- Work done per MHz compared with your baseline. If it drops, the card is clock stretching.

## Commands

```
GpuOcChecker stress   [--profile quick|standard|thorough] [--minutes N] [--phases light,transient,heavy,vram,bandwidth,idle]
                      [--settings "..."] [--save-baseline] [--vram-percent 60] [--open]
GpuOcChecker monitor  [--duration MIN]           # record while gaming; Q / Ctrl+C to stop
GpuOcChecker memtune                             # assisted VRAM clock sweep
GpuOcChecker analyze  <session folder | telemetry.csv | HWiNFO.csv>
GpuOcChecker events   [--days 14]                # TDR / WHEA / unexpected-reboot history
GpuOcChecker list | sensors [--raw]
GpuOcChecker stress --simulate undervolt|memory|thermal|crash|stable   # demo without risk
```

`analyze` also reads **HWiNFO64 CSV logs**. It lines up the log with the Windows event log, so a crash recorded in HWiNFO gets the same diagnosis.

Reports are saved to `Documents\GpuOcChecker\sessions\<date>_<mode>\` as `report.html` (with charts), `analysis.json`, `telemetry.csv` and the crash journal. Every analysis threshold can be overridden in `Documents\GpuOcChecker\thresholds.json`, for example `{ "hotspotDeltaWarn": 20 }`.

## Where the data comes from

| | Telemetry | Stress workload |
|---|---|---|
| AMD Radeon | ADL PMLog (`atiadlxx.dll`, the same interface as the AMD Software overlay): clocks, core/SoC/memory voltage, edge/hotspot/memory/VRM temperature, power, load, fan | OpenCL (included with AMD Software) |
| NVIDIA | NVML (`nvml.dll`): clocks, temperature, power and limit, load, fan, throttle reasons, PCIe replay count. **No core voltage.** | OpenCL |
| Intel / other | none (errors are still detected, but can't be attributed) | OpenCL |

No kernel drivers are installed and no administrator rights are needed.

## Limitations

- The tool **reads** your settings. It does not change them, so tuning stays in AMD Software or Afterburner.
- Passing a synthetic test is necessary but not sufficient. That is why `monitor` exists.
- GDDR6 error correction hides most memory errors, so a memory overclock can be past its limit without ever producing a pattern error. Use the baseline comparison and `memtune`.
- The ADL sensor index table follows AMD's `adl_defines.h`. If a sensor looks wrong on a new driver, `sensors --raw` lists every raw index the driver reports.

## Development

```
dotnet test          # 22 tests; the OpenCL kernel test runs when an OpenCL runtime (e.g. POCL) is present
dotnet run --project src/GpuOcChecker -- stress --simulate undervolt --minutes 2 -y
```

- `src/GpuOcChecker.Core` contains the telemetry backends, OpenCL kernels, stress engine, event log, session journal, analyzer and HTML report.
- `src/GpuOcChecker` is the Spectre.Console command-line app.
