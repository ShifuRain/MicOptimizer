# Third-party notices

Mic Optimizer uses the following components. Each remains under its own license.

## NAudio (MIT)

Audio device access (WASAPI, ASIO) and audio plumbing. <https://github.com/naudio/NAudio>

© Mark Heath 2026. Released under the MIT License (as stated in the NAudio 3.1.0 package metadata).

## RNNoise (BSD 3-Clause)

Recurrent-neural-network noise suppression. <https://gitlab.xiph.org/xiph/rnnoise>

Copyright notices, as published in the RNNoise repository's COPYING file:

- Copyright (c) 2007-2017, 2024 Jean-Marc Valin
- Copyright (c) 2023 Amazon
- Copyright (c) 2017, Mozilla
- Copyright (c) 2005-2017, Xiph.Org Foundation
- Copyright (c) 2003-2004, Mark Borgerding

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:

- Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
- Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.
- Neither the name of the Xiph.Org Foundation nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE FOUNDATION OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

(The bundled `rnnoise.dll` comes from the wrapper package below. This notice reproduces the license from the upstream repository.)

## YellowDogMan.RRNoise.NET (MIT)

A .NET wrapper that bundles prebuilt RNNoise binaries for Windows and Linux. <https://github.com/Yellow-Dog-Man/RNNoise.Net>

Copyright (c) 2023 Yellow Dog Man Studios S.r.o. Released under the MIT License. The bundled native `rnnoise.dll` is a prebuilt binary that has not been independently audited.

## Not included

- **VB-Cable** (VB-Audio) is a separate donationware product. It is installed by the user from <https://vb-audio.com/Cable/> and is not redistributed here.
- **Inno Setup** is used only to build the installer and is not part of it.
