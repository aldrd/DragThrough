# Third-party notices

DragThrough itself is licensed under CC BY-ND 4.0 (see [LICENSE](LICENSE)), which does **not**
permit publishing modified versions.

The components listed here are **not** covered by that license. They are the work of their
respective authors and remain under their own terms, which grant broader rights — including the
right to modify and redistribute them. Nothing in DragThrough's license restricts the rights you
receive under the licenses below.

---

## ManagedShell

Vendored into this repository under [`ManagedShell/`](ManagedShell/).

- **Upstream:** https://github.com/cairoshell/ManagedShell
- **License:** Apache License 2.0 — full text in [`ManagedShell/LICENSE`](ManagedShell/LICENSE)
- **Used for:** the secondary taskbar, window management and shell integration

The shell library behind [Cairo Shell](https://github.com/cairoshell/cairoshell) and
[RetroBar](https://github.com/dremin/RetroBar).

Apache 2.0 permits modification and redistribution, including in commercial and proprietary
products, provided the license and attribution are retained — which is what this file and
`ManagedShell/LICENSE` do.

---

## .NET runtime and libraries

DragThrough ships as a self-contained build, so the .NET runtime and the packages below are
distributed inside the application executable.

- **Publisher:** Microsoft
- **License:** MIT License
- **Upstream:** https://github.com/dotnet/runtime

Referenced packages:

| Package | Purpose |
| --- | --- |
| `Microsoft.Extensions.Hosting` | application host / lifetime |
| `System.Data.OleDb` | data access |
| `System.Drawing.Common` | icon and bitmap handling |
| `System.Security.Principal.Windows` | Windows identity and access checks |
| `System.ServiceProcess.ServiceController` | Windows service queries |

The MIT License permits use, modification and redistribution, including commercially, provided
the copyright notice and permission notice are retained.

---

## Reporting an omission

If you believe a component is missing from this list or is attributed incorrectly, please open an
issue — attribution problems are treated as bugs.
