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

## The installer's terminal packages

`hvo-sky-install`, the installer, is one self-contained file. Its wizard (#1317)
draws with Terminal.Gui, and the installer contains Terminal.Gui and every
package it brings in, as listed here. Every release ships this file beside the
installers as an asset of its own, `THIRD-PARTY-NOTICES.md` (install contract
section 4).

| Package | Licence | Copyright | Source |
| --- | --- | --- | --- |
| `Terminal.Gui` | MIT | Copyright 2007-2011 Novell Inc; Copyright 2017 Microsoft Corp | https://github.com/tui-cs/Terminal.Gui |
| `ColorHelper` | MIT | Copyright (c) 2020 Artyom Gritsuk | https://github.com/iamartyom/ColorHelper |
| `JetBrains.Annotations` | MIT | Copyright (c) 2016-2025 JetBrains s.r.o. | https://github.com/JetBrains/JetBrains.Annotations |
| `Markdig` | BSD-2-Clause, below | Copyright (c) 2016-2026, Alexandre Mutel | https://github.com/xoofx/markdig |
| `TextMateSharp` | MIT | Copyright (c) 2021 Daniel Peñalba | https://github.com/danipen/TextMateSharp |
| `TextMateSharp.Grammars` | MIT; its grammars under their own licences, below | Copyright (c) 2021 Daniel Peñalba | https://github.com/danipen/TextMateSharp |
| `Onigwrap` | MIT; Oniguruma under BSD-2-Clause, below | Copyright (c) 2024 Aikawa Yataro; Copyright (c) 2015 Fluent Solutions | https://github.com/aikawayataro/Onigwrap |
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

Onigwrap carries a native build of Oniguruma, the regular-expression library,
for each runtime, and the installer contains it (`libonigwrap`). The installer
never loads it: Terminal.Gui loads TextMateSharp's and Onigwrap's managed code,
but the wizard highlights no text, and a test checks that a wizard run never
maps the native library. Its notice is here because the library is in the file.
Onigwrap's own notices also name mono/SkiaSharp (Copyright (c) 2015-2016
Xamarin, Inc.; Copyright (c) 2017-2018 Microsoft Corporation; MIT).

The following MIT terms apply to each package and grammar above and below that
is under the MIT License, with its copyright notice:

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

### Oniguruma

Oniguruma is in the installer as Onigwrap's native library.

Copyright (c) 2002-2021  K.Kosako  <kkosako0@gmail.com>
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:
1. Redistributions of source code must retain the above copyright
   notice, this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright
   notice, this list of conditions and the following disclaimer in the
   documentation and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE AUTHOR AND CONTRIBUTORS ``AS IS'' AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED.  IN NO EVENT SHALL THE AUTHOR OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS
OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY
OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF
SUCH DAMAGE.

### The grammars in TextMateSharp.Grammars

TextMateSharp.Grammars 2.0.4 embeds these grammars, as its `cgmanifest.json`
files record them. Those under the MIT License take the MIT terms above, and
those under Apache-2.0 the Apache License, Version 2.0, below.

| Grammar | Upstream | Commit | Licence |
| --- | --- | --- | --- |
| `asciidoc` | https://github.com/mattneub/AsciiDoc-TextMate-2.tmbundle | `df611ad3df9b` | MIT |
| `bat` | https://github.com/mmims/language-batchfile | `6154ae25a24e` | MIT |
| `clojure` | https://github.com/atom/language-clojure | `45bdb881501d` | MIT |
| `coffeescript` | https://github.com/atom/language-coffee-script | `0f6db9143663` | MIT |
| `cpp` | https://github.com/jeff-hykin/better-cpp-syntax | `924295fc44bd` | MIT |
| `cpp` | https://github.com/jeff-hykin/better-c-syntax | `34712a6106a4` | MIT |
| `cpp` | https://github.com/textmate/c.tmbundle | `60daf83b9d45` | TextMate Bundle License |
| `cpp` | https://github.com/NVIDIA/cuda-cpp-grammar | `81e88eaec517` | MIT |
| `csharp` | https://github.com/dotnet/csharp-tmLanguage | `16612717ccd5` | MIT |
| `css` | https://github.com/atom/language-css | `4a6dc90f332b` | GitHub License |
| `dart` | https://github.com/dart-lang/dart-syntax-highlight | `9d4857e114b7` | BSD |
| `diff` | https://github.com/textmate/diff.tmbundle | `0593bb775eab` | TextMate Bundle License |
| `docker` | https://github.com/moby/moby | `abd39744c6f3` | Apache-2.0 |
| `fsharp` | https://github.com/ionide/ionide-fsgrammar | `e177bd7f9d34` | MIT |
| `git` | https://github.com/textmate/git.tmbundle | `5870cf3f8aba` | MIT |
| `git` | https://github.com/textmate/diff.tmbundle | `0593bb775eab` | TextMate Bundle License |
| `go` | https://github.com/jeff-hykin/better-go-syntax | `54ff898316f8` | MIT |
| `groovy` | https://github.com/textmate/groovy.tmbundle | `85d8f7c97ae4` | TextMate Bundle License |
| `handlebars` | https://github.com/daaain/Handlebars | `85a153a6f759` | MIT |
| `hlsl` | https://github.com/tgjones/shaders-tmLanguage | `87c0dca3a391` | MIT |
| `html` | https://github.com/textmate/html.tmbundle | `0c3d5ee54de3` | TextMate Bundle License |
| `ini` | https://github.com/textmate/ini.tmbundle | `2af0cbb07049` | TextMate Bundle License |
| `java` | https://github.com/atom/language-java | `29f977dc42a7` | MIT |
| `javascript` | https://github.com/microsoft/TypeScript-TmLanguage | `3133e3d914db` | MIT |
| `javascript` | https://github.com/textmate/javascript.tmbundle | `fccf0af0c954` | TextMate Bundle License |
| `json` | https://github.com/microsoft/vscode-JSON.tmLanguage | `9bd83f1c252b` | MIT |
| `julia` | https://github.com/JuliaEditorSupport/atom-language-julia | `7b7801f41ce4` | MIT |
| `latex` | https://github.com/jlelong/vscode-latex-basics | `7b08daed2296` | MIT |
| `less` | https://github.com/atom/language-less | `87d4d59e8de6` | MIT |
| `log` | https://github.com/emilast/vscode-logfile-highlighter | `8acba9307254` | MIT |
| `lua` | https://github.com/sumneko/lua.tmbundle | `bc74f9230c3f` | TextMate Bundle License |
| `make` | https://github.com/fadeevab/make.tmbundle | `ef0c485afc66` | TextMate Bundle License |
| `markdownbasics` | https://github.com/textmate/markdown.tmbundle | `11cf764606cb` | TextMate Bundle License |
| `markdownbasics` | https://github.com/microsoft/vscode-markdown-tm-grammar | `a612b96d62aa` | MIT |
| `markdownmath` | https://github.com/James-Yu/LaTeX-Workshop | `e4cd86f17315` | MIT |
| `objectivec` | https://github.com/jeff-hykin/cpp-textmate-grammar | `bc7dedd28eeb` | MIT |
| `pascal` | https://github.com/AnsonYeung/vscode-pascal-language-basics | `5faf4d487e08` | MIT |
| `perl` | https://github.com/textmate/perl.tmbundle | `a85927a902d6` | TextMate Bundle License |
| `php` | https://github.com/atom/language-php | `4cdf06a944a3` | MIT |
| `powershell` | https://github.com/PowerShell/EditorSyntax | `742f0b5d4b60` | MIT |
| `pug` | https://github.com/davidrios/pug-tmbundle | `e67e895f6fb6` | MIT |
| `python` | https://github.com/MagicStack/MagicPython | `c9b3409deb69` | MIT |
| `r` | https://github.com/Ikuyadeu/vscode-R | `ff60e426f665` | MIT |
| `razor` | https://github.com/demyte/language-cshtml | `e6e54d5a86a2` | MIT |
| `ruby` | https://github.com/textmate/ruby.tmbundle | `efcb8941c701` | TextMate Bundle License |
| `rust` | https://github.com/dustypomerleau/rust-syntax | `20462d50ff97` | MIT |
| `scss` | https://github.com/atom/language-sass | `f52ab12f7f93` | MIT |
| `shaderlab` | https://github.com/tgjones/shaders-tmLanguage | `c72c8b39380b` | MIT |
| `shellscript` | https://github.com/atom/language-shellscript | `4f8d7bb5cc4d` | MIT |
| `sql` | https://github.com/microsoft/vscode-mssql | `b8b58864526c` | MIT |
| `swift` | https://github.com/owensd/vscode-swift | `cec27af6662e` | MIT |
| `swift` | https://github.com/freebroccolo/atom-language-swift | `d8f4ed1f79cb` | MIT |
| `typescriptbasics` | https://github.com/microsoft/TypeScript-TmLanguage | `634a12f4fa2b` | MIT |
| `typst` | https://github.com/Myriad-Dreamin/tinymist | `c2aa453af444` | Apache-2.0 |
| `vb` | https://github.com/textmate/asp.vb.net.tmbundle | `72d44550b328` | TextMate Bundle License |
| `x86asm` | https://github.com/haxo-games/Universal-Assembly-Syntax-Highlighting | - | MIT |
| `xml` | https://github.com/atom/language-xml | `7bc75dfe779a` | MIT |
| `yaml` | https://github.com/textmate/yaml.tmbundle | `e54ceae3b719` | TextMate Bundle License |

The licence texts the grammars' manifests record, as they record them:

`asciidoc`, from https://github.com/mattneub/AsciiDoc-TextMate-2.tmbundle:

```text
Copyright (c) Matt Neuburg

Probably MIT (http://choosealicense.com/licenses/mit/).
This seems closest to the Markdown bundle license which is what I would want to imitate.
Do what you want with it, don't pretend you wrote it if you didn't, and good luck to you.
https://github.com/mattneub/AsciiDoc-TextMate-2.tmbundle/issues/2#issuecomment-72376749
```

`cpp`, from https://github.com/textmate/c.tmbundle:

```text
Copyright (c) textmate-c.tmbundle authors

If not otherwise specified (see below), files in this repository fall under the following license:

Permission to copy, use, modify, sell and distribute this
software is granted. This software is provided "as is" without
express or implied warranty, and with no claim as to its
suitability for any purpose.

An exception is made for files in readable text which contain their own license information,
or files where an accompanying file exists (in the same directory) with a "-license" suffix added
to the base-name name of the original file, and an extension of txt, html, or similar. For example
"tidy" is accompanied by "tidy-license.txt".
```

`css`, from https://github.com/atom/language-css:

```text
Copyright (c) 2014 GitHub Inc.

Permission is hereby granted, free of charge, to any person obtaining
a copy of this software and associated documentation files (the
"Software"), to deal in the Software without restriction, including
without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to
the following conditions:

The above copyright notice and this permission notice shall be
included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

--------------------------------------------------------------------

This package was derived from a TextMate bundle located at
https://github.com/textmate/css.tmbundle and distributed under the following
license, located in `README.mdown`:

Permission to copy, use, modify, sell and distribute this
software is granted. This software is provided "as is" without
express or implied warranty, and with no claim as to its
suitability for any purpose.
```

`dart`, from https://github.com/dart-lang/dart-syntax-highlight:

```text
Copyright 2020, the Dart project authors.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

    * Redistributions of source code must retain the above copyright
    notice, this list of conditions and the following disclaimer.
    * Redistributions in binary form must reproduce the above
    copyright notice, this list of conditions and the following
    disclaimer in the documentation and/or other materials provided
    with the distribution.
    * Neither the name of Google LLC nor the names of its
    contributors may be used to endorse or promote products derived
    from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

`diff`, from https://github.com/textmate/diff.tmbundle:

```text
Copyright (c) textmate-diff.tmbundle project authors

If not otherwise specified (see below), files in this repository fall under the following license:

Permission to copy, use, modify, sell and distribute this
software is granted. This software is provided "as is" without
express or implied warranty, and with no claim as to its
suitability for any purpose.

An exception is made for files in readable text which contain their own license information,
or files where an accompanying file exists (in the same directory) with a "-license" suffix added
to the base-name name of the original file, and an extension of txt, html, or similar. For example
"tidy" is accompanied by "tidy-license.txt".
```

`git`, from https://github.com/textmate/git.tmbundle:

```text
Copyright (c) 2008 Tim Harper

Permission is hereby granted, free of charge, to any person obtaining
a copy of this software and associated documentation files (the"
Software"), to deal in the Software without restriction, including
without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to
the following conditions:

The above copyright notice and this permission notice shall be
included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

`git`, from https://github.com/textmate/diff.tmbundle:

```text
Copyright (c) textmate-diff.tmbundle project authors

If not otherwise specified (see below), files in this repository fall under the following license:

Permission to copy, use, modify, sell and distribute this
software is granted. This software is provided "as is" without
express or implied warranty, and with no claim as to its
suitability for any purpose.

An exception is made for files in readable text which contain their own license information,
or files where an accompanying file exists (in the same directory) with a "-license" suffix added
to the base-name name of the original file, and an extension of txt, html, or similar. For example
"tidy" is accompanied by "tidy-license.txt".
```

`groovy`, from https://github.com/textmate/groovy.tmbundle:

```text
Copyright (c) textmate-groovy.tmbundle project authors

If not otherwise specified (see below), files in this repository fall under the following license:

Permission to copy, use, modify, sell and distribute this
software is granted. This software is provided "as is" without
express or implied warranty, and with no claim as to its
suitability for any purpose.

An exception is made for files in readable text which contain their own license information,
or files where an accompanying file exists (in the same directory) with a "-license" suffix added
to the base-name name of the original file, and an extension of txt, html, or similar. For example
"tidy" is accompanied by "tidy-license.txt".
```

`handlebars`, from https://github.com/daaain/Handlebars:

```text
-- Credits

Adapted from the great sublime-text-handlebars package by Nicholas Westlake.

Thanks a lot to all the generous contributors (in alphabetical order): @bittersweetryan, @bradcliffe, @calumbrodie, @duncanbeevers, @hlvnst, @jonschlinkert, @Krutius, @samselikoff, @utkarshkukreti, @zeppelin

-- License

(The MIT License)

Copyright (c) daaain/Handlebars project authors

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the 'Software'), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED 'AS IS', WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

`html`, from https://github.com/textmate/html.tmbundle:

```text
Copyright (c) textmate-html.tmbundle project authors

If not otherwise specified (see below), files in this repository fall under the following license:

Permission to copy, use, modify, sell and distribute this
software is granted. This software is provided "as is" without
express or implied warranty, and with no claim as to its
suitability for any purpose.

An exception is made for files in readable text which contain their own license information,
or files where an accompanying file exists (in the same directory) with a "-license" suffix added
to the base-name name of the original file, and an extension of txt, html, or similar. For example
"tidy" is accompanied by "tidy-license.txt".
```

`ini`, from https://github.com/textmate/ini.tmbundle:

```text
Copyright (c) textmate-ini.tmbundle project authors

If not otherwise specified (see below), files in this folder fall under the following license:

Permission to copy, use, modify, sell and distribute this
software is granted. This software is provided "as is" without
express or implied warranty, and with no claim as to its
suitability for any purpose.

An exception is made for files in readable text which contain their own license information,
or files where an accompanying file exists (in the same directory) with a "-license" suffix added
to the base-name name of the original file, and an extension of txt, html, or similar. For example
"tidy" is accompanied by "tidy-license.txt".
```

`javascript`, from https://github.com/textmate/javascript.tmbundle:

```text
Copyright (c) textmate-javascript.tmbundle project authors

If not otherwise specified (see below), files in this repository fall under the following license:

Permission to copy, use, modify, sell and distribute this
software is granted. This software is provided "as is" without
express or implied warranty, and with no claim as to its
suitability for any purpose.

An exception is made for files in readable text which contain their own license information,
or files where an accompanying file exists (in the same directory) with a "-license" suffix added
to the base-name name of the original file, and an extension of txt, html, or similar. For example
"tidy" is accompanied by "tidy-license.txt".
```

`latex`, from https://github.com/jlelong/vscode-latex-basics:

```text
Copyright (c) vscode-latex-basics authors

If not otherwise specified (see below), files in this repository fall under the MIT License


The file syntaxes/LaTeX.tmLanguage.json is based on https://github.com/textmate/latex.tmbundle/blob/master/Syntaxes/LaTeX.plist
but has been largely modified. The original file falls under the following license

Permission to copy, use, modify, sell and distribute this
software is granted. This software is provided "as is" without
express or implied warranty, and with no claim as to its
suitability for any purpose.

The file syntaxes/markdown-latex-combined.tmLanguage.json is generated from the Markdown grammar
included in VSCode and falls under the license described in markdown-latex-combined-license.txt.

The file syntaxes/cpp-grammar-bailout.tmLanguage.json is generated from https://github.com/jeff-hykin/better-cpp-syntax
and falls under the license described in cpp-bailout-license.txt.
```

`lua`, from https://github.com/sumneko/lua.tmbundle:

```text
Copyright (c) sumneko-lua.tmbundle project authors

If not otherwise specified (see below), files in this repository fall under the following license:

Permission to copy, use, modify, sell and distribute this
software is granted. This software is provided "as is" without
express or implied warranty, and with no claim as to its
suitability for any purpose.

An exception is made for files in readable text which contain their own license information,
or files where an accompanying file exists (in the same directory) with a "-license" suffix added
to the base-name name of the original file, and an extension of txt, html, or similar. For example
"tidy" is accompanied by "tidy-license.txt".
```

`make`, from https://github.com/fadeevab/make.tmbundle:

```text
Copyright (c) textmate-make.tmbundle project authors

If not otherwise specified (see below), files in this repository fall under the following license:

Permission to copy, use, modify, sell and distribute this
software is granted. This software is provided "as is" without
express or implied warranty, and with no claim as to its
suitability for any purpose.

An exception is made for files in readable text which contain their own license information,
or files where an accompanying file exists (in the same directory) with a "-license" suffix added
to the base-name name of the original file, and an extension of txt, html, or similar. For example
"tidy" is accompanied by "tidy-license.txt".
```

`markdownbasics`, from https://github.com/textmate/markdown.tmbundle:

```text
Copyright (c) markdown.tmbundle authors

If not otherwise specified (see below), files in this repository fall under the following license:

Permission to copy, use, modify, sell and distribute this
software is granted. This software is provided "as is" without
express or implied warranty, and with no claim as to its
suitability for any purpose.

An exception is made for files in readable text which contain their own license information,
or files where an accompanying file exists (in the same directory) with a "-license" suffix added
to the base-name name of the original file, and an extension of txt, html, or similar. For example
"tidy" is accompanied by "tidy-license.txt".
```

`perl`, from https://github.com/textmate/perl.tmbundle:

```text
Copyright (c) textmate-perl.tmbundle project authors

If not otherwise specified (see below), files in this repository fall under the following license:

Permission to copy, use, modify, sell and distribute this
software is granted. This software is provided "as is" without
express or implied warranty, and with no claim as to its
suitability for any purpose.

An exception is made for files in readable text which contain their own license information,
or files where an accompanying file exists (in the same directory) with a "-license" suffix added
to the base-name name of the original file, and an extension of txt, html, or similar. For example
"tidy" is accompanied by "tidy-license.txt".
```

`ruby`, from https://github.com/textmate/ruby.tmbundle:

```text
Copyright (c) textmate-ruby.tmbundle project authors

If not otherwise specified (see below), files in this folder fall under the following license:

Permission to copy, use, modify, sell and distribute this
software is granted. This software is provided "as is" without
express or implied warranty, and with no claim as to its
suitability for any purpose.

An exception is made for files in readable text which contain their own license information,
or files where an accompanying file exists (in the same directory) with a "-license" suffix added
to the base-name name of the original file, and an extension of txt, html, or similar. For example
"tidy" is accompanied by "tidy-license.txt".
```

`swift`, from https://github.com/owensd/vscode-swift:

```text
The MIT License (MIT)

Copyright (c) 2015 David Owens II

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

`swift`, from https://github.com/freebroccolo/atom-language-swift:

```text
The MIT License (MIT)

Copyright (c) 2014 Darin Morrison

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

`vb`, from https://github.com/textmate/asp.vb.net.tmbundle:

```text
Copyright (c) textmate-asp.vb.net.tmbundle project authors

If not otherwise specified (see below), files in this folder fall under the following license:

Permission to copy, use, modify, sell and distribute this
software is granted. This software is provided "as is" without
express or implied warranty, and with no claim as to its
suitability for any purpose.

An exception is made for files in readable text which contain their own license information,
or files where an accompanying file exists (in the same directory) with a "-license" suffix added
to the base-name name of the original file, and an extension of txt, html, or similar. For example
"tidy" is accompanied by "tidy-license.txt".
```

`yaml`, from https://github.com/textmate/yaml.tmbundle:

```text
Copyright (c) 2015 FichteFoll <fichtefoll2@googlemail.com>

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

### Apache License, Version 2.0

The Typst grammar (Myriad-Dreamin/tinymist) and the Dockerfile grammar
(moby/moby) are under the Apache License, Version 2.0. Its text, as tinymist
gives it:

```text
                                 Apache License
                           Version 2.0, January 2004
                        http://www.apache.org/licenses/

   TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION

   1. Definitions.

      "License" shall mean the terms and conditions for use, reproduction,
      and distribution as defined by Sections 1 through 9 of this document.

      "Licensor" shall mean the copyright owner or entity authorized by
      the copyright owner that is granting the License.

      "Legal Entity" shall mean the union of the acting entity and all
      other entities that control, are controlled by, or are under common
      control with that entity. For the purposes of this definition,
      "control" means (i) the power, direct or indirect, to cause the
      direction or management of such entity, whether by contract or
      otherwise, or (ii) ownership of fifty percent (50%) or more of the
      outstanding shares, or (iii) beneficial ownership of such entity.

      "You" (or "Your") shall mean an individual or Legal Entity
      exercising permissions granted by this License.

      "Source" form shall mean the preferred form for making modifications,
      including but not limited to software source code, documentation
      source, and configuration files.

      "Object" form shall mean any form resulting from mechanical
      transformation or translation of a Source form, including but
      not limited to compiled object code, generated documentation,
      and conversions to other media types.

      "Work" shall mean the work of authorship, whether in Source or
      Object form, made available under the License, as indicated by a
      copyright notice that is included in or attached to the work
      (an example is provided in the Appendix below).

      "Derivative Works" shall mean any work, whether in Source or Object
      form, that is based on (or derived from) the Work and for which the
      editorial revisions, annotations, elaborations, or other modifications
      represent, as a whole, an original work of authorship. For the purposes
      of this License, Derivative Works shall not include works that remain
      separable from, or merely link (or bind by name) to the interfaces of,
      the Work and Derivative Works thereof.

      "Contribution" shall mean any work of authorship, including
      the original version of the Work and any modifications or additions
      to that Work or Derivative Works thereof, that is intentionally
      submitted to Licensor for inclusion in the Work by the copyright owner
      or by an individual or Legal Entity authorized to submit on behalf of
      the copyright owner. For the purposes of this definition, "submitted"
      means any form of electronic, verbal, or written communication sent
      to the Licensor or its representatives, including but not limited to
      communication on electronic mailing lists, source code control systems,
      and issue tracking systems that are managed by, or on behalf of, the
      Licensor for the purpose of discussing and improving the Work, but
      excluding communication that is conspicuously marked or otherwise
      designated in writing by the copyright owner as "Not a Contribution."

      "Contributor" shall mean Licensor and any individual or Legal Entity
      on behalf of whom a Contribution has been received by Licensor and
      subsequently incorporated within the Work.

   2. Grant of Copyright License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      copyright license to reproduce, prepare Derivative Works of,
      publicly display, publicly perform, sublicense, and distribute the
      Work and such Derivative Works in Source or Object form.

   3. Grant of Patent License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      (except as stated in this section) patent license to make, have made,
      use, offer to sell, sell, import, and otherwise transfer the Work,
      where such license applies only to those patent claims licensable
      by such Contributor that are necessarily infringed by their
      Contribution(s) alone or by combination of their Contribution(s)
      with the Work to which such Contribution(s) was submitted. If You
      institute patent litigation against any entity (including a
      cross-claim or counterclaim in a lawsuit) alleging that the Work
      or a Contribution incorporated within the Work constitutes direct
      or contributory patent infringement, then any patent licenses
      granted to You under this License for that Work shall terminate
      as of the date such litigation is filed.

   4. Redistribution. You may reproduce and distribute copies of the
      Work or Derivative Works thereof in any medium, with or without
      modifications, and in Source or Object form, provided that You
      meet the following conditions:

      (a) You must give any other recipients of the Work or
          Derivative Works a copy of this License; and

      (b) You must cause any modified files to carry prominent notices
          stating that You changed the files; and

      (c) You must retain, in the Source form of any Derivative Works
          that You distribute, all copyright, patent, trademark, and
          attribution notices from the Source form of the Work,
          excluding those notices that do not pertain to any part of
          the Derivative Works; and

      (d) If the Work includes a "NOTICE" text file as part of its
          distribution, then any Derivative Works that You distribute must
          include a readable copy of the attribution notices contained
          within such NOTICE file, excluding those notices that do not
          pertain to any part of the Derivative Works, in at least one
          of the following places: within a NOTICE text file distributed
          as part of the Derivative Works; within the Source form or
          documentation, if provided along with the Derivative Works; or,
          within a display generated by the Derivative Works, if and
          wherever such third-party notices normally appear. The contents
          of the NOTICE file are for informational purposes only and
          do not modify the License. You may add Your own attribution
          notices within Derivative Works that You distribute, alongside
          or as an addendum to the NOTICE text from the Work, provided
          that such additional attribution notices cannot be construed
          as modifying the License.

      You may add Your own copyright statement to Your modifications and
      may provide additional or different license terms and conditions
      for use, reproduction, or distribution of Your modifications, or
      for any such Derivative Works as a whole, provided Your use,
      reproduction, and distribution of the Work otherwise complies with
      the conditions stated in this License.

   5. Submission of Contributions. Unless You explicitly state otherwise,
      any Contribution intentionally submitted for inclusion in the Work
      by You to the Licensor shall be under the terms and conditions of
      this License, without any additional terms or conditions.
      Notwithstanding the above, nothing herein shall supersede or modify
      the terms of any separate license agreement you may have executed
      with Licensor regarding such Contributions.

   6. Trademarks. This License does not grant permission to use the trade
      names, trademarks, service marks, or product names of the Licensor,
      except as required for reasonable and customary use in describing the
      origin of the Work and reproducing the content of the NOTICE file.

   7. Disclaimer of Warranty. Unless required by applicable law or
      agreed to in writing, Licensor provides the Work (and each
      Contributor provides its Contributions) on an "AS IS" BASIS,
      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
      implied, including, without limitation, any warranties or conditions
      of TITLE, NON-INFRINGEMENT, MERCHANTABILITY, or FITNESS FOR A
      PARTICULAR PURPOSE. You are solely responsible for determining the
      appropriateness of using or redistributing the Work and assume any
      risks associated with Your exercise of permissions under this License.

   8. Limitation of Liability. In no event and under no legal theory,
      whether in tort (including negligence), contract, or otherwise,
      unless required by applicable law (such as deliberate and grossly
      negligent acts) or agreed to in writing, shall any Contributor be
      liable to You for damages, including any direct, indirect, special,
      incidental, or consequential damages of any character arising as a
      result of this License or out of the use or inability to use the
      Work (including but not limited to damages for loss of goodwill,
      work stoppage, computer failure or malfunction, or any and all
      other commercial damages or losses), even if such Contributor
      has been advised of the possibility of such damages.

   9. Accepting Warranty or Additional Liability. While redistributing
      the Work or Derivative Works thereof, You may choose to offer,
      and charge a fee for, acceptance of support, warranty, indemnity,
      or other liability obligations and/or rights consistent with this
      License. However, in accepting such obligations, You may act only
      on Your own behalf and on Your sole responsibility, not on behalf
      of any other Contributor, and only if You agree to indemnify,
      defend, and hold each Contributor harmless for any liability
      incurred by, or claims asserted against, such Contributor by reason
      of your accepting any such warranty or additional liability.

   END OF TERMS AND CONDITIONS

   APPENDIX: How to apply the Apache License to your work.

      To apply the Apache License to your work, attach the following
      boilerplate notice, with the fields enclosed by brackets "[]"
      replaced with your own identifying information. (Don't include
      the brackets!)  The text should be enclosed in the appropriate
      comment syntax for the file format. We also recommend that a
      file or class name and description of purpose be included on the
      same "printed page" as the copyright notice for easier
      identification within third-party archives.

   Copyright 2023-2025 Myriad Dreamin, Nathan Varner

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
```

moby/moby's NOTICE, at the grammar's commit:

```text
Docker
Copyright 2012-2017 Docker, Inc.

This product includes software developed at Docker, Inc. (https://www.docker.com).

This product contains software (https://github.com/kr/pty) developed
by Keith Rarick, licensed under the MIT License.

The following is courtesy of our legal counsel:


Use and transfer of Docker may be subject to certain restrictions by the
United States and other governments.
It is your responsibility to ensure that your use and/or transfer does not
violate applicable laws.

For more information, please see https://www.bis.doc.gov

See also https://www.apache.org/dev/crypto.html and/or seek legal counsel.
```
