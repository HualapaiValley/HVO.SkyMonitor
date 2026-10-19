# Third-Party Notices

## Astronomy Engine

HVO.SkyMonitor uses Astronomy Engine 2.1.19 for offline solar-system ephemeris
calculations.

Copyright (c) 2019-2025 Don Cross <cosinekitty@gmail.com>

Licensed under the MIT License:

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to do
so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

Source: https://github.com/cosinekitty/astronomy/tree/v2.1.19

## D3-Celestial Constellation Data

HVO.SkyMonitor derives constellation line topology from D3-Celestial v0.7.32.

Copyright (c) 2015, Olaf Frohn
All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.
3. Neither the name of the copyright holder nor the names of its contributors
   may be used to endorse or promote products derived from this software without
   specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

Source: https://github.com/ofrohn/d3-celestial/tree/v0.7.32/data

## HYG Database

HVO.SkyMonitor includes a derived bright-star subset of the HYG Database 4.2 for
offline virtual-camera operation and deterministic tests.

Copyright (c) David Nash and the catalog contributors.

Licensed under the Creative Commons Attribution-ShareAlike 4.0 International
License: https://creativecommons.org/licenses/by-sa/4.0/

Source: https://astronexus.com/projects/hyg

The included SQLite subset, its source rows, checksum, and reproducible
derivation are documented in `tests/fixtures/catalog/SOURCE.md` and
`docs/catalog/hyg-v42.md`. Modifications and redistributions of the catalog data
remain subject to the same attribution and ShareAlike terms.

## OpenNGC

HVO.SkyMonitor's tests include a small derived excerpt of OpenNGC v20260501,
composed with HYG 4.4 stars, to exercise the deep-sky catalog collection.

Copyright (c) 2023 Mattia Verga and the OpenNGC contributors.

Licensed under the Creative Commons Attribution-ShareAlike 4.0 International
License: https://creativecommons.org/licenses/by-sa/4.0/

Source: https://github.com/mattiaverga/OpenNGC at commit
`36cb178a0f69dba8bfc03a99c10512831edf1c6b`

The excerpt, its source rows, checksums, and reproducible derivation are
documented in `tests/fixtures/catalog/SOURCE-v44-openngc.md`, and the complete
upstream acknowledgements in `docs/catalog/hyg-v44-openngc-attribution.md`.
Modifications and redistributions of the catalog data remain subject to the same
attribution and ShareAlike terms.

## ZWO ASI Camera SDK

The optional `HVO.SkyMonitor.CameraAgent.Modules.Zwo` adapter contains C ABI
declarations derived from the official ZWO ASI Camera SDK V1.41
`ASICamera2.h`. Copyright for the SDK, header, and binary belongs to ZWO /
Suzhou ZWO Co., Ltd. Use and redistribution are subject to the license included
with the official SDK distribution.

No ZWO binary, header, udev rule, or SDK license file is distributed in this
repository. Operators must obtain the SDK and license directly from ZWO. The
reviewed V1.41 license file has SHA-256
`98ad1c18048bfdabc8463740ac36a8d8cd710bdc3102ac6c22978ec50056e5a2`.

Source: https://www.zwoastro.com/software/product-sdk/

## Bootstrap

HVO.SkyMonitor distributes Bootstrap 5.3.8 CSS for offline CameraAgent browser
operation.

Copyright (c) 2011-2025 The Bootstrap Authors.

Licensed under the MIT License. Source:
https://github.com/twbs/bootstrap/tree/v5.3.8

## Bootstrap Icons

HVO.SkyMonitor distributes Bootstrap Icons 1.11.3 CSS and fonts for offline
CameraAgent browser operation.

Copyright (c) 2019-2024 The Bootstrap Authors.

Licensed under the MIT License. Source:
https://github.com/twbs/icons/tree/v1.11.3

The following MIT terms apply to the Bootstrap and Bootstrap Icons copies
described above:

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## The terminal programs' packages

`hvo-sky-install`, the installer, and `hvo-sky`, CameraAgent's command line,
are each one self-contained file. The installer's wizard and `hvo-sky ui`
(#1317) draw with Terminal.Gui, and both files contain Terminal.Gui and the
packages listed here, which Terminal.Gui brings in; `hvo-sky` also contains
System.CommandLine. Every release ships this file beside them as an asset of its
own, `THIRD-PARTY-NOTICES.md` (install contract section 4).

Terminal.Gui also depends on TextMateSharp, its grammars and Onigwrap's native
Oniguruma library, for the syntax highlighting of its Code and Markdown views.
No terminal program here highlights text, so the installer and `hvo-sky` leave
them out and none of their files are in either (`Directory.Build.targets`).

| Package | Licence | Copyright | Source |
| --- | --- | --- | --- |
| `Terminal.Gui` | MIT | Copyright 2007-2011 Novell Inc; Copyright 2017 Microsoft Corp | https://github.com/tui-cs/Terminal.Gui |
| `ColorHelper` | MIT | Copyright (c) 2020 Artyom Gritsuk | https://github.com/iamartyom/ColorHelper |
| `JetBrains.Annotations` | MIT | Copyright (c) 2016-2025 JetBrains s.r.o. | https://github.com/JetBrains/JetBrains.Annotations |
| `Markdig` | BSD-2-Clause, below | Copyright (c) 2016-2026, Alexandre Mutel | https://github.com/xoofx/markdig |
| `Wcwidth` | MIT | Copyright Patrik Svensson. Phil Scott | https://github.com/spectreconsole/wcwidth |
| `System.IO.Abstractions` | MIT | Copyright (c) Tatham Oddie and Contributors | https://github.com/TestableIO/System.IO.Abstractions |
| `TestableIO.System.IO.Abstractions` | MIT | Copyright (c) Tatham Oddie and Contributors | https://github.com/TestableIO/System.IO.Abstractions |
| `TestableIO.System.IO.Abstractions.Wrappers` | MIT | Copyright (c) Tatham Oddie and Contributors | https://github.com/TestableIO/System.IO.Abstractions |
| `Testably.Abstractions.FileSystem.Interface` | MIT | Copyright (c) 2022 Valentin Breuß | https://github.com/Testably/Testably.Abstractions |
| `Microsoft.Extensions.Configuration` | MIT | Copyright (c) .NET Foundation and Contributors | https://github.com/dotnet/runtime |
| `Microsoft.Extensions.Configuration.Abstractions` | MIT | Copyright (c) .NET Foundation and Contributors | https://github.com/dotnet/runtime |
| `Microsoft.Extensions.Configuration.Binder` | MIT | Copyright (c) .NET Foundation and Contributors | https://github.com/dotnet/runtime |
| `Microsoft.Extensions.Configuration.FileExtensions` | MIT | Copyright (c) .NET Foundation and Contributors | https://github.com/dotnet/runtime |
| `Microsoft.Extensions.Configuration.Json` | MIT | Copyright (c) .NET Foundation and Contributors | https://github.com/dotnet/runtime |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | MIT | Copyright (c) .NET Foundation and Contributors | https://github.com/dotnet/runtime |
| `Microsoft.Extensions.FileProviders.Abstractions` | MIT | Copyright (c) .NET Foundation and Contributors | https://github.com/dotnet/runtime |
| `Microsoft.Extensions.FileProviders.Physical` | MIT | Copyright (c) .NET Foundation and Contributors | https://github.com/dotnet/runtime |
| `Microsoft.Extensions.FileSystemGlobbing` | MIT | Copyright (c) .NET Foundation and Contributors | https://github.com/dotnet/runtime |
| `Microsoft.Extensions.Logging.Abstractions` | MIT | Copyright (c) .NET Foundation and Contributors | https://github.com/dotnet/runtime |
| `Microsoft.Extensions.Options` | MIT | Copyright (c) .NET Foundation and Contributors | https://github.com/dotnet/runtime |
| `Microsoft.Extensions.Primitives` | MIT | Copyright (c) .NET Foundation and Contributors | https://github.com/dotnet/runtime |
| `System.CommandLine` | MIT | Copyright (c) .NET Foundation and Contributors | https://github.com/dotnet/command-line-api |

The following MIT terms apply to each package above that is under the MIT
License, with its copyright notice:

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

### Markdig

Copyright (c) 2016-2026, Alexandre Mutel
All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
