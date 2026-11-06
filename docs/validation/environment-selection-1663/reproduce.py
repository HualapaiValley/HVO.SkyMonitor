"""Measure the retained default payload pair and check the committed diagnostic pixels."""
import os
from pathlib import Path
import shutil
import subprocess
import sys
from xml.sax.saxutils import escape

scratch = Path(sys.argv[1]).resolve()
if not scratch.is_dir():
    raise SystemExit("Pass an existing hvo-recorded scratch directory.")
if os.environ.get("HVO_ENVIRONMENT_MEASURE_LOCKED") != "1":
    os.execvp("flock", ["flock", "-w", "900", "-o", "/tmp/hvo-heavy-gate.lock", "timeout", "300",
                       "env", "HVO_ENVIRONMENT_MEASURE_LOCKED=1", sys.executable, __file__, str(scratch)])
here = Path(__file__).resolve().parent
repo = here.parents[2]
directory = scratch / "environment-measure"
directory.mkdir(exist_ok=False)
shutil.copyfile(repo / "Directory.Packages.props", directory / "Directory.Packages.props")
shutil.copyfile(here / "Measure.cs.txt", directory / "Program.cs")
assemblies = ["HVO.SkyMonitor.AgentCore", "HVO.SkyMonitor.Imaging", "HVO.SkyMonitor.Astronomy", "HVO.SkyMonitor.Processing"]
references = "".join(f'<Reference Include="{name}"><HintPath>{escape(str(repo / "src" / name / "bin/Release/net10.0" / (name + ".dll")))}</HintPath></Reference>' for name in assemblies)
(directory / "Measure.csproj").write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup>' + references + '<PackageReference Include="SkiaSharp" /><PackageReference Include="SkiaSharp.NativeAssets.Linux.NoDependencies" /></ItemGroup></Project>')
environment = os.environ | {"DOTNET_PROCESSOR_COUNT": "2", "MSBUILDDISABLENODEREUSE": "1", "UseSharedCompilation": "false"}
with (directory / "build.log").open("w") as log:
    subprocess.run(["dotnet", "build", str(directory / "Measure.csproj"), "--configuration", "Release", "-warnaserror",
                    "-nodeReuse:false", "-p:UseSharedCompilation=false"], cwd=repo, env=environment, stdout=log, stderr=subprocess.STDOUT, check=True)
program = directory / "bin/Release/net10.0/Measure.dll"
for name, extra in [("interleaved.json", []), ("image-checks.json", ["--images"])]:
    with (directory / name).open("w") as result:
        subprocess.run(["dotnet", str(program), str(here), *extra], cwd=repo, env=environment, stdout=result, check=True)
print(directory)
