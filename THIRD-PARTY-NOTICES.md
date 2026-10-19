# Third-party notices

This file lists the third-party components that Queuey distributes from this
repository, or that its packages depend on, with each component's version,
licence and source. It also reproduces the licence texts and notices those
licences require. Queuey's own code is licensed under the MIT License; see
[LICENSE](LICENSE).

Versions are those resolved for release **v0.1.0-preview.12**.

## What each artefact contains

| Artefact | Published on | Third-party code inside |
|---|---|---|
| `Queuey.Client`, `Queuey.Client.Waas`, `Queuey.Edge`, `Queuey.Edge.Mqtt` | NuGet.org | None. Their third-party components are NuGet dependencies, which NuGet restores from nuget.org under each component's own licence. |
| `Queuey.Cli` (a .NET tool package) | NuGet.org | Every managed assembly the tool runs on, and the native SQLite library for each platform SQLitePCLRaw builds it for. It does not contain the .NET runtime; the tool runs on the runtime installed on the machine. |
| Self-contained `queuey` binaries (`linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64`, `win-x64`) | GitHub Releases | The same managed assemblies as `Queuey.Cli`, the native SQLite library for the binary's own platform, and the .NET runtime. |

The tables below use two columns for this:

- **Dependency of** — the Queuey library packages that depend on the
  component, directly or transitively. `Client`, `Waas`, `Edge` and
  `Edge.Mqtt` stand for `Queuey.Client`, `Queuey.Client.Waas`,
  `Queuey.Edge` and `Queuey.Edge.Mqtt`. `Waas` and `Edge` depend on
  `Client`, and `Edge.Mqtt` depends on `Edge`, so each also inherits the
  dependencies of the package it builds on. A target framework in brackets
  means the dependency applies to that target only.
- **Inside** — the Queuey artefacts that contain a copy of the component.

## Components

### SQLite

Public domain; see <https://sqlite.org/copyright.html>. Source:
<https://sqlite.org>.

| Component | Version | Licence | Dependency of | Inside |
|---|---|---|---|---|
| SQLite, compiled as the `e_sqlite3` native library and shipped in `SQLitePCLRaw.lib.e_sqlite3` | 3.53.3 | Public domain | `Edge` | `Queuey.Cli`, binaries |

### SQLitePCLRaw

Apache License 2.0. Copyright 2014-2024 SourceGear, LLC. Source:
<https://github.com/ericsink/SQLitePCL.raw>, commit
[`0873d14`](https://github.com/ericsink/SQLitePCL.raw/tree/0873d14f6dcb3a8537444679468499ab9f5a782a)
(version 2.1.13, branch `maint_21`). The licence text and the notices from
SQLitePCLRaw's NOTICE file are reproduced under
[Apache License 2.0 — SQLitePCLRaw](#apache-license-20--sqlitepclraw).

| Component | Version | Licence | Dependency of | Inside |
|---|---|---|---|---|
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.13 | Apache-2.0 | `Edge` | `Queuey.Cli`, binaries |
| SQLitePCLRaw.core | 2.1.13 | Apache-2.0 | `Edge` | `Queuey.Cli`, binaries |
| SQLitePCLRaw.provider.e_sqlite3 | 2.1.13 | Apache-2.0 | `Edge` | `Queuey.Cli`, binaries |
| SQLitePCLRaw.lib.e_sqlite3 (packages the SQLite native library above) | 2.1.13 | Apache-2.0 | `Edge` | `Queuey.Cli`, binaries |

`Queuey.Edge` pins `SQLitePCLRaw.bundle_e_sqlite3` 2.1.13 explicitly, above the
version `Microsoft.Data.Sqlite` 8.0.10 would select on its own; see
`src/Queuey.Edge/Queuey.Edge.csproj`.

### MQTTnet

MIT License. Copyright (c) .NET Foundation and Contributors. Source:
<https://github.com/dotnet/MQTTnet>, tag `v5.2.0`.

| Component | Version | Licence | Dependency of | Inside |
|---|---|---|---|---|
| MQTTnet | 5.2.0.1603 | MIT | `Edge.Mqtt` | `Queuey.Cli`, binaries |

### Microsoft.Data.Sqlite

MIT License. Copyright (c) .NET Foundation and Contributors; the packages
carry © Microsoft Corporation. Source: <https://github.com/dotnet/efcore>,
tag `v8.0.10`.

| Component | Version | Licence | Dependency of | Inside |
|---|---|---|---|---|
| Microsoft.Data.Sqlite (contains no code; references Microsoft.Data.Sqlite.Core and SQLitePCLRaw.bundle_e_sqlite3) | 8.0.10 | MIT | `Edge` | — |
| Microsoft.Data.Sqlite.Core | 8.0.10 | MIT | `Edge` | `Queuey.Cli`, binaries |

### ASP.NET Core SignalR client

MIT License. Copyright (c) .NET Foundation and Contributors; the packages
carry © Microsoft Corporation. Source: <https://github.com/dotnet/aspnetcore>,
tag `v8.0.11`. Used by the CLI only.

| Component | Version | Licence | Dependency of | Inside |
|---|---|---|---|---|
| Microsoft.AspNetCore.SignalR.Client | 8.0.11 | MIT | — | `Queuey.Cli`, binaries |
| Microsoft.AspNetCore.SignalR.Client.Core | 8.0.11 | MIT | — | `Queuey.Cli`, binaries |
| Microsoft.AspNetCore.SignalR.Common | 8.0.11 | MIT | — | `Queuey.Cli`, binaries |
| Microsoft.AspNetCore.SignalR.Protocols.Json | 8.0.11 | MIT | — | `Queuey.Cli`, binaries |
| Microsoft.AspNetCore.Http.Connections.Client | 8.0.11 | MIT | — | `Queuey.Cli`, binaries |
| Microsoft.AspNetCore.Http.Connections.Common | 8.0.11 | MIT | — | `Queuey.Cli`, binaries |
| Microsoft.AspNetCore.Connections.Abstractions | 8.0.11 | MIT | — | `Queuey.Cli`, binaries |
| Microsoft.Extensions.Features | 8.0.11 | MIT | — | `Queuey.Cli`, binaries |

### .NET libraries

MIT License. Copyright (c) .NET Foundation and Contributors; the packages
carry © Microsoft Corporation. Source: <https://github.com/dotnet/runtime>,
except the four packages marked `corefx`, which were built from
<https://github.com/dotnet/corefx> (archived).

| Component | Version | Licence | Dependency of | Inside |
|---|---|---|---|---|
| Microsoft.Bcl.AsyncInterfaces | 8.0.0 | MIT | `Client` (netstandard2.0) | — |
| Microsoft.Extensions.Configuration | 8.0.0 | MIT | `Client` (net8.0) | `Queuey.Cli`, binaries |
| Microsoft.Extensions.Configuration.Abstractions | 8.0.0 | MIT | `Client` | `Queuey.Cli`, binaries |
| Microsoft.Extensions.Configuration.Binder | 8.0.0 | MIT | `Client` (net8.0) | — |
| Microsoft.Extensions.Configuration.Binder | 8.0.2 | MIT | — | `Queuey.Cli`, binaries |
| Microsoft.Extensions.Configuration.CommandLine | 8.0.0 | MIT | — | `Queuey.Cli`, binaries |
| Microsoft.Extensions.Configuration.EnvironmentVariables | 8.0.0 | MIT | — | `Queuey.Cli`, binaries |
| Microsoft.Extensions.Configuration.FileExtensions | 8.0.1 | MIT | — | `Queuey.Cli`, binaries |
| Microsoft.Extensions.Configuration.Json | 8.0.1 | MIT | — | `Queuey.Cli`, binaries |
| Microsoft.Extensions.Configuration.UserSecrets | 8.0.1 | MIT | — | `Queuey.Cli`, binaries |
| Microsoft.Extensions.DependencyInjection | 8.0.1 | MIT | `Client` | `Queuey.Cli`, binaries |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 | MIT | `Client` | `Queuey.Cli`, binaries |
| Microsoft.Extensions.Diagnostics | 8.0.1 | MIT | `Client` (net8.0) | `Queuey.Cli`, binaries |
| Microsoft.Extensions.Diagnostics.Abstractions | 8.0.1 | MIT | `Client` (net8.0), `Waas` | `Queuey.Cli`, binaries |
| Microsoft.Extensions.FileProviders.Abstractions | 8.0.0 | MIT | `Waas`, `Edge` | `Queuey.Cli`, binaries |
| Microsoft.Extensions.FileProviders.Physical | 8.0.0 | MIT | — | `Queuey.Cli`, binaries |
| Microsoft.Extensions.FileSystemGlobbing | 8.0.0 | MIT | — | `Queuey.Cli`, binaries |
| Microsoft.Extensions.Hosting | 8.0.1 | MIT | — | `Queuey.Cli`, binaries |
| Microsoft.Extensions.Hosting.Abstractions | 8.0.1 | MIT | `Waas`, `Edge`, `Edge.Mqtt` | `Queuey.Cli`, binaries |
| Microsoft.Extensions.Http | 8.0.1 | MIT | `Client`, `Waas` | `Queuey.Cli`, binaries |
| Microsoft.Extensions.Logging | 8.0.1 | MIT | `Client` | `Queuey.Cli`, binaries |
| Microsoft.Extensions.Logging.Abstractions | 8.0.2 | MIT | `Client`, `Edge`, `Edge.Mqtt` | `Queuey.Cli`, binaries |
| Microsoft.Extensions.Logging.Configuration | 8.0.1 | MIT | — | `Queuey.Cli`, binaries |
| Microsoft.Extensions.Logging.Console | 8.0.1 | MIT | — | `Queuey.Cli`, binaries |
| Microsoft.Extensions.Logging.Debug | 8.0.1 | MIT | — | `Queuey.Cli`, binaries |
| Microsoft.Extensions.Logging.EventLog | 8.0.1 | MIT | — | `Queuey.Cli`, binaries |
| Microsoft.Extensions.Logging.EventSource | 8.0.1 | MIT | — | `Queuey.Cli`, binaries |
| Microsoft.Extensions.Options | 8.0.2 | MIT | `Client` | `Queuey.Cli`, binaries |
| Microsoft.Extensions.Options.ConfigurationExtensions | 8.0.0 | MIT | `Client` (net8.0) | `Queuey.Cli`, binaries |
| Microsoft.Extensions.Primitives | 8.0.0 | MIT | `Client` | `Queuey.Cli`, binaries |
| System.Buffers (`corefx`) | 4.5.1 | MIT | `Client` (netstandard2.0) | — |
| System.ComponentModel.Annotations | 5.0.0 | MIT | `Client` (netstandard2.0) | — |
| System.Diagnostics.DiagnosticSource | 8.0.1 | MIT | `Client` (netstandard2.0) | — |
| System.Diagnostics.EventLog | 8.0.1 | MIT | — | `Queuey.Cli`, binaries |
| System.IO.Pipelines | 8.0.0 | MIT | — | `Queuey.Cli`, binaries |
| System.Memory (`corefx`) | 4.5.5 | MIT | `Client` (netstandard2.0) | — |
| System.Memory (`corefx`) | 4.5.3 | MIT | `Edge` (through SQLitePCLRaw.core; on net8.0 the .NET runtime supplies it) | — |
| System.Numerics.Vectors (`corefx`) | 4.4.0 | MIT | `Client` (netstandard2.0) | — |
| System.Runtime.CompilerServices.Unsafe | 6.0.0 | MIT | `Client` (netstandard2.0) | — |
| System.Text.Encodings.Web | 8.0.0 | MIT | `Client` (netstandard2.0) | — |
| System.Text.Json | 8.0.5 | MIT | `Client`, `Waas` (netstandard2.0) | — |
| System.Threading.Tasks.Extensions (`corefx`) | 4.5.4 | MIT | `Client` (netstandard2.0) | — |

### .NET runtime

MIT License. Copyright (c) .NET Foundation and Contributors. Source:
<https://github.com/dotnet/runtime>, tag `v8.0.32` for v0.1.0-preview.12.

| Component | Version | Licence | Dependency of | Inside |
|---|---|---|---|---|
| .NET runtime (`Microsoft.NETCore.App`: the runtime, the base class libraries and the single-file host) | 8.0.32 | MIT | — | binaries |

Each release's binaries carry the newest .NET 8 servicing release that the
release build's .NET SDK publishes for `net8.0`. v0.1.0-preview.12 was built
with SDK 9.0.319, which publishes 8.0.32.

### Not distributed

These are resolved during a build, but no Queuey package or binary contains
them and no Queuey package declares them as dependencies:

- `Microsoft.SourceLink.GitHub` 8.0.0, with `Microsoft.SourceLink.Common` and
  `Microsoft.Build.Tasks.Git` 8.0.0 (MIT): build-time only; they add source
  links to the symbols.
- `Microsoft.NET.ILLink.Tasks` (MIT): .NET SDK tooling used while publishing
  the binaries.
- `NETStandard.Library` 2.0.3, the netstandard2.0 reference assemblies the
  libraries compile against, and `Microsoft.NETCore.Platforms` 1.1.0, which it
  references.
- `System.Threading.Channels` 8.0.0: requested by the SignalR client; the
  .NET runtime supplies it, so the package is not used.
- The test and sample projects in this repository, which are not packaged.

## Licence texts and notices

### MIT License — .NET Foundation and Contributors

Applies to the components built from dotnet/runtime, dotnet/corefx,
dotnet/aspnetcore and dotnet/efcore listed above, and to the .NET runtime.
The four repositories carry the same licence text:

```text
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

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
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

Those repositories also carry notices for third-party code incorporated into
them. The notices apply to the components built from them, and form part of
this notice:

- .NET runtime and libraries:
  <https://github.com/dotnet/runtime/blob/v8.0.32/THIRD-PARTY-NOTICES.TXT>.
- The `corefx` packages:
  <https://github.com/dotnet/corefx/blob/master/THIRD-PARTY-NOTICES.TXT>.
- ASP.NET Core:
  <https://github.com/dotnet/aspnetcore/blob/v8.0.11/THIRD-PARTY-NOTICES.txt>.
- dotnet/efcore has no third-party notices file at `v8.0.10`.

Every package listed under .NET libraries and ASP.NET Core SignalR client also
contains the notices file of the repository it was built from, as
`THIRD-PARTY-NOTICES.TXT`.

### MIT License — MQTTnet

```text
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors
All Rights Reserved

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
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### Apache License 2.0 — SQLitePCLRaw

SQLitePCLRaw is Copyright 2014-2024 SourceGear, LLC, and licensed under the
Apache License, Version 2.0. Queuey redistributes the SQLitePCLRaw packages'
files unmodified and does not change their licence.

#### NOTICE

SQLitePCLRaw includes a NOTICE file. As section 4(d) of the Apache License
requires, the attribution notices from that file, as of the 2.1.13 commit
([`NOTICE.TXT`](https://github.com/ericsink/SQLitePCL.raw/blob/0873d14f6dcb3a8537444679468499ab9f5a782a/NOTICE.TXT)),
are reproduced below.

The file's sections "License for SQLCipher" and "License for OpenSSL
libcrypto" are omitted, as section 4(d) allows for notices that do not
pertain to the distributed work. They cover SQLitePCLRaw's SQLCipher builds,
and Queuey distributes only the `e_sqlite3` build, which contains neither
SQLCipher nor OpenSSL.

```text
----------------------------------------------------------------
Copyright on SQLitePCL.raw
----------------------------------------------------------------

Version prior to 2.0 were labeled with the copyright owned by
Zumero.  In 2.0, this changed to SourceGear.  There is no legal
distinction, as Zumero is simply a dba name for SourceGear.

And in either case, the open source license remains the same,
Apache v2.

----------------------------------------------------------------
License for SQLite
----------------------------------------------------------------

** The author disclaims copyright to this source code.  In place of
** a legal notice, here is a blessing:
**
**    May you do good and not evil.
**    May you find forgiveness for yourself and forgive others.
**    May you share freely, never taking more than you give.
**


----------------------------------------------------------------
License for MS Open Tech
----------------------------------------------------------------

// Copyright © Microsoft Open Technologies, Inc.
// All Rights Reserved
// Licensed under the Apache License, Version 2.0 (the "License"); you may not
// use this file except in compliance with the License. You may obtain a copy
// of the License at
// http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABLITY OR NON-INFRINGEMENT.
//
// See the Apache 2 License for the specific language governing permissions and
// limitations under the License.
```

#### Licence text

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

   Copyright [yyyy] [name of copyright owner]

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
