# Third-party components

- **Vortice.DirectInput / Vortice.DirectX 3.8.3** — [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows), copyright Amer Koleci and contributors, MIT license: https://github.com/amerkoleci/Vortice.Windows/blob/main/LICENSE . Used as a managed binding to Windows DirectInput; distributed through NuGet with the application.
- **SharpGen.Runtime / SharpGen.Runtime.COM 2.4.2-beta** — [SharpGenTools](https://github.com/SharpGenTools/SharpGenTools), MIT license: https://github.com/SharpGenTools/SharpGenTools/blob/main/LICENSE.txt . Transitive runtime dependencies of Vortice.
- **.NET 8 Windows Desktop Runtime** — Microsoft and contributors; MIT and other notices distributed in the self-contained runtime. See `Microsoft.NETCore.App-*` and `Microsoft.WindowsDesktop.App-*` license/notice files in the published folder.

The DirectInput proxy uses Windows SDK interface declarations and dynamically calls the installed Microsoft system DLL, which is not bundled. WiiInput is neither bundled nor referenced. HidHide is not required; legacy code remains for recovery of prior Controller Tweak hiding leases.

## MIT notices

The following copyright notices accompany the MIT-licensed components above:

Vortice: Copyright (c) Amer Koleci and Contributors.

SharpGen: Copyright (c) 2010-2017 Alexandre Mutel, 2017-2023 Jeremy Koritzinsky, 2023-2024 Amer Koleci (package metadata); the upstream license also credits 2010-2017 Alexandre Mutel and 2017 Jeremy Koritzinsky.

Microsoft System.IO.Pipelines, System.Text.Json, and System.Text.Encodings.Web are transitive MIT-licensed .NET components: Copyright (c) .NET Foundation and Contributors.

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
