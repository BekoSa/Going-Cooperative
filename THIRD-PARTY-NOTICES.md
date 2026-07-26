# Third-party notices

The Going Cooperative Windows release is a convenience bundle. It redistributes
the unmodified official `BepInEx_win_x64_5.4.23.5.zip` files so players can
install the mod and its loader in one extraction.

Official BepInEx release:

- Version: 5.4.23.5, Windows x64
- Release: https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5
- Asset: `BepInEx_win_x64_5.4.23.5.zip`
- SHA-256: `82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4`
- Source: https://github.com/BepInEx/BepInEx/tree/v5.4.23.5
- License: MIT; see `Licenses/BepInEx-5.4.23.5-MIT.txt`

The upstream BepInEx distribution contains the following third-party
components. Going Cooperative does not modify these binaries:

| Component | Bundled version | License | Source and license copy |
| --- | --- | --- | --- |
| Unity Doorstop | 4.5.0 | LGPL-2.1 | [Source](https://github.com/NeighTools/UnityDoorstop/tree/v4.5.0); `Licenses/UnityDoorstop-4.5.0-LGPL-2.1.txt` |
| HarmonyX | 2.9.0 | MIT | [Source](https://github.com/BepInEx/HarmonyX/tree/v2.9.0); `Licenses/HarmonyX-2.9.0-MIT.txt` |
| Harmony | 2.0.0 compatibility assembly | MIT | [Source](https://github.com/pardeike/Harmony); `Licenses/Harmony-2.0.0-MIT.txt` |
| BepInEx.Harmony compatibility layer | commit `d4cdcb4` | MIT | [Source](https://github.com/BepInEx/BepInEx.Harmony/tree/d4cdcb4cdeac14a0b77012165f5f5a9f5032a9fa); `Licenses/BepInEx.Harmony-d4cdcb4-MIT.txt` |
| MonoMod.RuntimeDetour and MonoMod.Utils | 22.1.29.1 | MIT | [Source](https://github.com/MonoMod/MonoMod/tree/v22.01.29.01); `Licenses/MonoMod-22.1.29.1-MIT.txt` |
| Mono.Cecil | 0.10.4 | MIT | [Source](https://github.com/jbevain/cecil/tree/0.10.4); `Licenses/Mono.Cecil-0.10.4-MIT.txt` |

The release archive includes the complete applicable license texts under
`Licenses/`. It also includes the exact corresponding Unity Doorstop v4.5.0
source archive at
`Licenses/Source/UnityDoorstop-v4.5.0-source.zip` (SHA-256
`7f0c963104aa08bf5fefef8ff85e7fecd8306838f5af3101487d9db4e9188d63`).
Unity Doorstop remains a replaceable shared bootstrap component.

Going Medieval and Unity are not redistributed. Their names and trademarks
belong to their respective owners. Going Cooperative is an unofficial community
project and is not affiliated with or endorsed by The Irregular Corporation,
Foxy Voxel, BepInEx, or Unity Technologies.
