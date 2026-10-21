# Third-Party Notices

Lingarr is free software distributed under the **GNU Affero General Public License v3.0 (AGPL-3.0)**;
the complete license text for this product is included in the repository root as [`LICENSE`](./LICENSE).

This document lists the **direct third-party dependencies** of Lingarr that are distributed with the
source code and inside the public container images published via GHCR, together with the license that
governs each of them. License identifiers follow the [SPDX](https://spdx.org/licenses/) standard.
Versions are transcribed from [`Directory.Packages.props`](./Directory.Packages.props) (server, NuGet
central package management) and [`Lingarr.Client/package.json`](./Lingarr.Client/package.json)
(frontend, npm) in this source tree.

Entries marked **(unverified)** reflect our best-effort identification at the time of writing and
should be confirmed against the package page on nuget.org / npmjs.com before formal redistribution
review. Complete license texts and copyright notices for every package listed here are available from
the official package pages on [nuget.org](https://www.nuget.org/) and [npmjs.com](https://www.npmjs.com/),
or from the respective upstream source repositories.

---

## 1. Server (.NET) — runtime dependencies (distributed inside the GHCR image)

| Package | Version | License | Notes |
|---|---|---|---|
| DeepL.net | 1.22.1 | MIT | Official DeepL API client for .NET. Copyright DeepL SE. |
| EFCore.NamingConventions | 10.0.1 | MIT | Copyright Shay Rojansky and contributors. |
| FluentMigrator | 8.0.1 | Apache-2.0 | Copyright FluentMigrator Project contributors. |
| FluentMigrator.Runner | 8.0.1 | Apache-2.0 | Copyright FluentMigrator Project contributors. |
| FluentMigrator.Runner.SQLite | 8.0.1 | Apache-2.0 | Copyright FluentMigrator Project contributors. |
| GTranslate | 2.4.0 | MIT | Translation service abstraction library. |
| Hangfire | 1.8.25 | **LGPL-3.0** | Background job framework. Copyright Sergey Odinokov and Hangfire contributors. Distributed under the GNU Lesser General Public License v3.0 — not MIT. |
| Hangfire.MemoryStorage | 1.8.1.2 | MIT | In-memory storage for Hangfire, referenced by the server host. |
| Hangfire.Storage.SQLite | 0.4.3 | MIT | SQLite-backed storage for Hangfire. Copyright 2019 RaisedApp. |
| MathNet.Numerics | 5.0.0 | MIT | Numerical computation library (FFT cross-correlation for the timeline sync, B05). Copyright Math.NET Project contributors. |
| Microsoft.AspNetCore.SpaServices.Extensions | 10.0.12 | MIT | Copyright .NET Foundation and Contributors. |
| Microsoft.Data.Sqlite | 10.0.12 | MIT | Copyright .NET Foundation and Contributors. Ships a native SQLite bundle via SQLitePCLRaw; **SQLite itself is Public Domain** (see section 5). |
| Microsoft.EntityFrameworkCore | 10.0.12 | MIT | Copyright .NET Foundation and Contributors. |
| Microsoft.EntityFrameworkCore.Sqlite | 10.0.12 | MIT | Copyright .NET Foundation and Contributors. |
| Microsoft.Extensions.Logging.Abstractions | 10.0.0 | MIT | Copyright .NET Foundation and Contributors. Central version pin; referenced transitively. |
| OpenccNetLib | 1.4.0 | MIT | .NET wrapper around OpenCC (Open Chinese Convert). The bundled OpenCC dictionaries and data are Apache-2.0 (Copyright BYVoid and OpenCC contributors). |
| SharpCompress | 0.38.2 | MIT | Compression/archive library (used for subtitle archive handling). Copyright Adam Hathcock and contributors. |
| Swashbuckle.AspNetCore | 9.0.6 | MIT | Swagger/OpenAPI tooling for ASP.NET Core. Swashbuckle contributors. |
| Ude.NetStandard | 1.2.0 | (unverified) | C# port of Mozilla Universal Charset Detector. The package publishes no SPDX license expression ("see license directory" per its nuget license page); upstream Mozilla code carries MPL/LGPL heritage — confirm the package license files before redistribution review. |

## 2. Server (.NET) — development, design-time and test dependencies (NOT distributed in the runtime image)

These packages are referenced by the solution but are used only at build, design or test time. They do
not ship in the published GHCR image; they are listed here for completeness of the central package
manifest.

| Package | Version | License | Notes |
|---|---|---|---|
| Microsoft.AspNetCore.SpaProxy | 10.0.12 | MIT | Development-time SPA proxy. Copyright .NET Foundation and Contributors. |
| Microsoft.EntityFrameworkCore.Design | 10.0.12 | MIT | Design-time components. Copyright .NET Foundation and Contributors. |
| Microsoft.EntityFrameworkCore.InMemory | 10.0.12 | MIT | Test-only EF Core provider. Copyright .NET Foundation and Contributors. |
| Microsoft.EntityFrameworkCore.Tools | 10.0.12 | MIT | Design-time tooling. Copyright .NET Foundation and Contributors. |
| Microsoft.NET.Test.Sdk | 18.10.1 | MIT | Copyright .NET Foundation and Contributors. |
| Microsoft.VisualStudio.Azure.Containers.Tools.Targets | 1.23.0 | MIT | Build-time MSBuild targets for container tooling; no runtime artifacts. |
| Moq | 4.21.0 | BSD-3-Clause | Copyright The Moq Team. |
| xunit.runner.visualstudio | 3.1.5 | Apache-2.0 | xUnit.net test adapter. |
| xunit.v3 | 3.2.2 | Apache-2.0 | xUnit.net testing framework. |

## 3. Frontend (npm) — runtime dependencies (bundled into the built client assets shipped in the image)

The production build output of `Lingarr.Client` is embedded in the distributed image; the following
libraries (or parts thereof) are compiled or bundled into those assets.

| Package | Version | License | Notes |
|---|---|---|---|
| @fontsource-variable/archivo | ^5.3.0 | OFL-1.1 | Archivo variable typeface by Omnibus-Type, distributed under the SIL Open Font License 1.1; Fontsource packaging metadata is MIT. |
| @fontsource-variable/jetbrains-mono | ^5.3.0 | OFL-1.1 | JetBrains Mono variable typeface by JetBrains s.r.o., SIL Open Font License 1.1; Fontsource packaging metadata is MIT. |
| @microsoft/signalr | ^10.0.11 | MIT | SignalR JavaScript client. Copyright .NET Foundation and Contributors. |
| axios | ^1.20.0 | MIT | Copyright Matt Zabriskie and axios contributors. |
| chart.js | ^4.5.1 | MIT | Copyright Chart.js contributors. |
| cron-validator | ^1.4.0 | MIT | Cron expression validation. |
| pinia | ^4.0.3 | MIT | Copyright Eduardo San Martin Morote and contributors. |
| vue | ^3.5.43 | MIT | Copyright Yuxi (Evan) You and Vue contributors. |
| vue-chartjs | ^5.3.4 | MIT | Chart.js integration for Vue. |
| vue-i18n | ^11.4.14 | MIT | Copyright Kazuya Kawaguchi and contributors. |
| vue-router | ^5.3.1 | MIT | Copyright Eduardo San Martin Morote and contributors. |

## 4. Frontend (npm) — development tools (NOT distributed in the image)

Build, lint, format and test tooling used only at development time. None of these are compiled into
the shipped client assets.

| Package | Version | License | Notes |
|---|---|---|---|
| @oxfmt/binding-linux-x64-gnu | ^0.71.0 | MIT | Optional platform binding for oxfmt (Linux x64). |
| @oxfmt/binding-win32-x64-msvc | ^0.71.0 | MIT | Optional platform binding for oxfmt (Windows x64). |
| @tailwindcss/vite | ^4.3.3 | MIT | Tailwind CSS v4 Vite plugin. Copyright Tailwind Labs. |
| @types/node | ^26.6.3 | MIT | DefinitelyTyped contributors. |
| @vitejs/plugin-vue | ^6.0.9 | MIT | Vite plugin for Vue SFC compilation. |
| @vue/test-utils | ^2.5.1 | MIT | Test utilities for Vue. |
| happy-dom | ^21.0.1 | MIT | DOM emulation for tests. |
| oxfmt | ^0.71.0 | MIT | Formatter from the Oxc (oxidized compiler) project. |
| oxlint | ^1.86.0 | MIT | Linter from the Oxc (oxidized compiler) project. |
| tailwindcss | ^4.2.4 | MIT | Copyright Tailwind Labs. |
| typescript | ^6.0.3 | Apache-2.0 | Copyright Microsoft Corporation. |
| vite | ^8.3.1 | MIT | Copyright Vite contributors. |
| vitest | ^5.0.3 | MIT | Copyright Vite contributors. |
| vue-tsc | ^3.3.11 | MIT | Type-checking for Vue SFCs, part of Vue language tools. |

## 5. Container base images, embedded runtimes and transitive dependencies

- The published runtime image is based on `mcr.microsoft.com/dotnet/aspnet:10.0`. The .NET runtime is
  distributed by Microsoft under the **MIT** license; the underlying Debian base image and its
  components carry their own licenses (Debian Free Software Guidelines compliant).
- A native **SQLite** library (Public Domain) is embedded via the SQLitePCLRaw bundle pulled in by
  `Microsoft.Data.Sqlite`.
- **Code-page encodings** for subtitle charset detection are provided by
  `System.Text.Encoding.CodePages`, which is **not a separate NuGet dependency** here — no package
  reference exists in `Directory.Packages.props` or the project files; the `CodePagesEncodingProvider`
  type resolves from the .NET shared runtime itself (MIT, Copyright .NET Foundation and Contributors)
  and is registered programmatically at startup.
- **Transitive dependencies** of the packages listed above are redistributed under their own licenses
  as declared on their nuget.org/npmjs.com package pages; complete texts are available there. This
  document intentionally enumerates direct dependencies only.

## 6. License of this product

Lingarr itself is licensed under the **GNU Affero General Public License v3.0 (AGPL-3.0)**. The full
license text is included verbatim in the repository root: [`LICENSE`](./LICENSE). In particular,
section 13 of the AGPL-3.0 (Remote Network Interaction) applies to this product.

---

Last updated: 2026-10-15. If a dependency is added, removed or upgraded, update the corresponding
sections of this document in the same change set.
