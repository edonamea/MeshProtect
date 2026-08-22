<div align="center">

<img src="https://raw.githubusercontent.com/edonamea/MeshProtect/main/.github/images/banner-zh.png" alt="MeshProtect — 扒走了，也用不了。" width="100%">

[English](README.md) · [日本語](README.ja.md) · **简体中文** · [한국어](README.ko.md)

[![Unity 2022.3](https://img.shields.io/badge/Unity-2022.3-222222?style=flat-square&logo=unity&logoColor=white)](https://unity.com/)
[![VRChat Avatar SDK3](https://img.shields.io/badge/VRChat-Avatar%20SDK3-00acc1?style=flat-square)](https://vrchat.com/)
[![lilToon 等 7 种](https://img.shields.io/badge/lilToon-%E7%AD%89%207%20%E7%A7%8D-e91e63?style=flat-square)](#支持哪些-shader)
[![BOOTH](https://img.shields.io/badge/BOOTH-%E5%85%8D%E8%B4%B9-fc4d50?style=flat-square)](https://humuhumuhumu.booth.pm/items/8731588)
[![授权](https://img.shields.io/badge/%E6%8E%88%E6%9D%83-%E6%BA%90%E7%A0%81%E5%8F%AF%E8%A7%81-607d8b?style=flat-square)](LICENSE)

</div>

---

有人把你的模型从游戏文件里扒出来，在 Blender 里打开，看到的是一团彼此无关的点。这就是这个插件
要做的全部事情。

MeshProtect 在上传途中把网格打乱，同时附上一段能还原它的 shader —— 但只有穿着它的人从表情菜单
输入六位密码之后才会还原。**你的工程不会被碰。** 保护只施加在 SDK 构建时创建的临时副本上，
所以把组件摘掉，下一次上传就是普通模型，场景那边甚至不知道发生过什么。

<div align="center">
<img src="https://raw.githubusercontent.com/edonamea/MeshProtect/main/.github/images/locked-zh.png" alt="没有密码时模型完全不显示，输入之后恢复正常" width="92%">
<br><sub>锁定时模型根本不渲染 —— 而不是把打乱的网格摆在别人面前。</sub>
</div>

## 安装

在 [BOOTH](https://humuhumuhumu.booth.pm/items/8731588) 下载 `.unitypackage` 导入（免费），
或者把本仓库作为 UPM / VPM 包添加：

```
https://github.com/edonamea/MeshProtect.git
```

Unity 2022.3 · VRChat Avatar SDK3 · lilToon 2.x · PC 版模型
面板支持 **日本語 / English / 简体中文 / 한국어**，跟随编辑器语言自动切换。

## 四步

1. 选中模型根对象 → `Add Component` → `MeshProtect / Mesh Protect Root`
2. 点 **「生成密码」** —— 六位，每位 1–8；也可以自己输 1 到 6 位。
   **一定要记下来**：VRChat 的密码是按本机保存的，换电脑就要重新输一次
3. 照常 Build & Publish
4. 在 VRChat 里：Expressions → **Unlock** → 逐位选数字

> [!WARNING]
> 上传前 SDK 会报一条红色错误，说这个组件会被客户端移除。这是事实、无害、也在预期之内 ——
> 组件会在构建时从 SDK 复制出的临时副本上摘掉。
> **千万不要按 Auto Fix。** 它会把组件从你的场景里删掉，密码也跟着一起没。

<div align="center">
<img src="https://raw.githubusercontent.com/edonamea/MeshProtect/main/.github/images/inspector.png" alt="Mesh Protect Root 面板" width="60%">
<br><sub>这就是全部界面。没有烘焙按钮，场景里不会多出第二个模型，改完模型也没有要重跑的东西。</sub>
</div>

换装、PhysBone、Modular Avatar、VRCFury、网格优化工具都能一起用，顺序也不用管 —— 保护在这一切
之后才执行。Quest 版上传会原样通过，两边都不用去动那个勾。

## 支持哪些 shader

| | |
|---|---|
| **lilToon 2.x** | 原生支持。走的是 lilToon 官方的扩展点，没有 fork 也没有打补丁，所以 lilToon 更新不会把它弄坏。 |
| **Poiyomi Toon** · **Xiexe's Toon Shader** · **UnityChanToonShader** · **Sunao Shader** · **GTAvaToon** · **blackbody** | 自动移植：把生成的解码逐个 pass 复制到它们 shader 的副本上，靠语义定位，而不是照着文本打补丁。 |

移植没法完整覆盖的材质会**不加保护原样发出**并在控制台点名，而不是被弄坏。支持列表之外的
shader 完全不碰 —— 不会损坏。

## 挡得住什么

| | |
|---|---|
| 把扒到的网格在 Blender 里打开 | **挡得住** —— 得先把 shader 逆出来 |
| 拆开、重贴图、再拿去卖 | **挡得住** —— 同上 |
| 拿现成的解保护脚本跑一遍 | **挡得住** —— 没有两个模型共用一套算法 |
| 用任何 DCC 工具重新导出网格 | **挡得住** —— 顶点身份是 UV0 的原始比特 |
| 从 FX 控制器读出这个模型会做什么 | **挡得住** —— 图层、状态、混合树、动画剪辑都改了名 |
| 读网格、材质、物体叫什么 | **挡得住** —— 名字由模型自己的算法生成 |
| 手工逆向这个模型的 shader | 要花几个小时，**而且对下一个模型毫无帮助** |

目标从来不是「破不了」，而是**破解不成规模**：每一个模型都要把成本原封不动再付一遍。这才是真正
挡掉那些脚本化、离线、批量的攻击 —— 模型一旦流传开，实际发生的绝大多数就是这类。

贴图不受保护；有人和你在同一个实例里，也仍然能在你解锁穿着时从 GPU 抓走还原后的网格。这两条
限制和背后的理由都写在 [THREAT_MODEL_CN.md](THREAT_MODEL_CN.md) 里 —— 拿它去卖东西之前值得读一遍。

## 它会自己检查自己

这个领域的工具通常是悄无声息地失败，等你进游戏才发现。这一个在上传结束之前就验证：把生成的
shader 和 C# 的密码算法放到 **GPU 上**逐一比对，把每一个烘焙过的网格连同混合形状的各帧逐顶点
解回来核对，确认解锁菜单和它的传输位齐全，再把每个组件的每一个序列化字段走一遍，证明原始网格
在任何地方都不可达。凡是没能完整覆盖的都会原样保留并在控制台点名 —— 构建绝不会悄悄产出一个
坏掉的模型。

## 工作原理

- **位移是生成出来的，不是存下来的。** 每个顶点沿切线和法线被推开的距离，来自密码和该顶点自身
  身份的哈希。网格里没有任何可以读回来的系数。
- **每个模型都有自己的算法。** 设置密码时会现场组装一套新的哈希程序，并生成一整个把常量编译进去
  的 shader 家族。属性名、参数名、资产名也都是生成的 —— 上传出去的模型里连这个工具的名字都没有。
- **锁定的模型是隐形，不是炸开。** 所有顶点塌缩到一个点，任何 pass 都不渲染。密码输错也一样塌缩：
  模型要么正确，要么不存在，绝不会以乱掉的样子出现在别人面前。
- **全程整数运算**，所以 C# 和 HLSL 在任何 GPU 上都逐比特一致。

**开销：** 一个 UV 通道（TEXCOORD6）· 24 bit 表情参数预算 · 几条顶点着色器指令，
**不影响模型的 Performance Rank** · 生成算法时一次性约 3.5 秒。改密码和重新上传都不花这个时间。

## 更多

📄 **[THREAT_MODEL_CN.md](THREAT_MODEL_CN.md)** —— 技术说明、威胁模型、面板逐项说明、排障
📘 **[QUICK_START_CN.md](QUICK_START_CN.md)** —— 随包附带的中文说明书
📋 **[CHANGELOG.md](CHANGELOG.md)** —— 更新记录
💬 提问和反馈 —— [BOOTH 商品页](https://humuhumuhumu.booth.pm/items/8731588)的联系表单

## 授权

不是开源授权，见 [LICENSE](LICENSE)：可以自由用在自己的模型上，用它做出来的东西可以自由售卖，
但不可以再分发这个工具本身。

`Shaders/Templates` 里的 lilToon 模板衍生自
[lilxyzw/lilToon](https://github.com/lilxyzw/lilToon)（MIT），沿用该授权。位移的做法参考了
[rygo6/GTAvaCrypt](https://github.com/rygo6/GTAvaCrypt) 和
[lilxyzw/AvaterEncryption](https://github.com/lilxyzw/AvaterEncryption)（均为 MIT）的先行工作。
完整的归属说明见 [NOTICE.md](NOTICE.md)。
