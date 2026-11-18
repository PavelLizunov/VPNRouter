"""Record the worker and the test runner bundled with its selected .NET SDK."""
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys


def output(*args):
    return subprocess.check_output(args, text=True).strip()


print(f"{os.cpu_count()} logical CPUs, {shutil.disk_usage('.').free // (1024 ** 2)} MiB free on build volume", flush=True)
if os.name == "nt":
    subprocess.run(["powershell", "-NoProfile", "-Command", """
        $cpu = (Get-CimInstance Win32_Processor).LoadPercentage -join ', '
        $ram = (Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory
        $swap = (Get-CimInstance Win32_PageFileUsage | Measure-Object CurrentUsage -Sum).Sum
        Write-Output "CPU load $cpu percent, free RAM $ram KiB, pagefile usage $([int64]$swap) MiB"
        Get-Process -Name dotnet,msbuild,java,VBCSCompiler -ErrorAction SilentlyContinue | Select-Object ProcessName,Id
        exit 0
    """], check=True)
else:
    subprocess.run(["uptime"], check=True)
    if sys.platform == "darwin":
        subprocess.run(["vm_stat"], check=True)
        subprocess.run(["sysctl", "vm.swapusage"], check=True)
    else:
        subprocess.run(["free", "-h"], check=True)
    jobs = [line for line in output("ps", "-Ao", "pid=,comm=").splitlines()
            if Path(line.split(maxsplit=1)[1]).name.lower() in {"dotnet", "msbuild", "java", "vbcscompiler"}]
    print("Build processes:", jobs)

sdk = output("dotnet", "--version")
checkout = output("git", "rev-parse", "HEAD")
for line in output("dotnet", "--list-sdks").splitlines():
    match = re.fullmatch(r"(\S+) \[(.+)\]", line)
    if match and match[1] == sdk:
        manifest = Path(match[2]) / sdk / "vstest.console.deps.json"
        libraries = json.loads(manifest.read_text(encoding="utf-8-sig"))["libraries"]
        runner, = (name for name in libraries if name.startswith("vstest.console/"))
        print(f"{checkout}: .NET SDK {sdk}, bundled {runner}")
        break
else:
    raise SystemExit(f"Could not locate selected SDK {sdk}")
