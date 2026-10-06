# Deployment dependency notices

The deployment builds MiCamera.Net locally; it does not republish a combined Miloco image.
Before use or redistribution, review the licenses of the exact downloaded packages and images.

- Xiaomi Miloco has a purpose-limited license, including restrictions on developing other applications and web services. Personal/non-commercial use alone does not establish authorization for this integration. Obtain clarification or permission where necessary. The script requires an acknowledgement, which is not a license grant.
  https://github.com/XiaoMi/xiaomi-miloco/blob/main/LICENSE.md
- The miiot/miloco image is a community image, not an official Xiaomi image. Its upstream source version and embedded notices must be checked before redistribution.
  https://github.com/miiot/micam/blob/main/Dockerfile.miloco
- SIPSorcery 10.0.17: BSD 3-Clause with additional geographic/use restrictions. Its NuGet package contains the full applicable LICENSE.md.
  https://github.com/sipsorcery-org/sipsorcery/blob/4ca86773993a875706d8a7a4c1e0108e3a885ebf/LICENSE.md
- FFmpeg packages with libx264/libx265 include GPL-covered components. Local use is distinct from redistribution. Preserve notices and satisfy applicable source/relinking obligations before distributing images.
  https://ffmpeg.org/legal.html
- .NET, Python, Node.js, Nginx, Debian packages and NuGet/npm dependencies retain their respective licenses. Debian package notices are available under /usr/share/doc; NuGet/npm package notices remain in the build dependencies.

No statement here replaces the applicable licenses or confirms legal authorization.
