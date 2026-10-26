# Third-party notices

Wavelit itself is licensed under the GNU GPL v3 (see `LICENSE`). It includes or downloads the following
third-party components, each under its own licence.

## Shipped with Wavelit

| Component | Licence |
|---|---|
| Microsoft Edge WebView2 SDK | Microsoft Software License (redistributable) |
| ONNX Runtime (`Microsoft.ML.OnnxRuntime.DirectML`) | MIT, © Microsoft Corporation |
| DirectML (`Microsoft.AI.DirectML`) | Microsoft DirectML License (redistributable) |
| WPF-UI, H.NotifyIcon, CommunityToolkit.Mvvm, VirtualizingWrapPanel | MIT |
| Serilog, Microsoft.Extensions.* | Apache-2.0 / MIT |
| **Real-ESRGAN x4 (compact) model**, `src/Wavelit.Create/Models/realesrgan-x4` | BSD 3-Clause, see below |

### Real-ESRGAN: BSD 3-Clause License

Copyright (c) 2021, Xintao Wang. All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.
3. Neither the name of the copyright holder nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

## Downloaded only if the user opts in (Local AI pack)

| Component | Source | Licence |
|---|---|---|
| SDXL-Lightning (4-step), ONNX export | `huggingface.co/d0gr/sdxl-lightning-onnx-webgpu` @ `c0129962` | CreativeML Open RAIL++-M (inherited from SDXL 1.0 and ByteDance SDXL-Lightning), which includes use restrictions the user accepts before downloading |
| NSFW image classifier (ViT, int8 ONNX) | `huggingface.co/onnx-community/nsfw_image_detection-ONNX` @ `1ceb3c7f` | Apache-2.0 (Falconsai/nsfw_image_detection) |

## Content from online sources

Media found through Discover belongs to its creators and is shown with attribution: Wallhaven uploads (rights belong to the uploader; for personal use), NASA (generally public domain; no endorsement), Openverse (the Creative Commons licence on each card), Pixabay (Pixabay Content License), Pexels (Pexels License) and Shadertoy (CC BY-NC-SA 3.0 unless the author says otherwise).
