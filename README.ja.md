<div align="center">

<img src="https://raw.githubusercontent.com/edonamea/MeshProtect/main/.github/images/banner-ja.png" alt="MeshProtect — 抜かれても、使えない。" width="100%">

[English](README.md) · **日本語** · [简体中文](README.zh-CN.md) · [한국어](README.ko.md)

[![Unity 2022.3](https://img.shields.io/badge/Unity-2022.3-222222?style=flat-square&logo=unity&logoColor=white)](https://unity.com/)
[![VRChat Avatar SDK3](https://img.shields.io/badge/VRChat-Avatar%20SDK3-00acc1?style=flat-square)](https://vrchat.com/)
[![lilToon ほか 6 種](https://img.shields.io/badge/lilToon-%E3%81%BB%E3%81%8B%206%20%E7%A8%AE-e91e63?style=flat-square)](#対応シェーダー)
[![BOOTH](https://img.shields.io/badge/BOOTH-%E7%84%A1%E6%96%99-fc4d50?style=flat-square)](https://humuhumuhumu.booth.pm/items/8731588)
[![ライセンス](https://img.shields.io/badge/%E3%83%A9%E3%82%A4%E3%82%BB%E3%83%B3%E3%82%B9-MIT-607d8b?style=flat-square)](LICENSE)

</div>

---

誰かがあなたのアバターをゲームのファイルから抜き出し、Blender で開いて、そこにあるのは無関係な
点の集まりだけ。それがこのツールの目的のすべてです。

MeshProtect はアップロードの途中でメッシュを崩し、それを元に戻すシェーダーを同梱します。戻せる
のは、Expression メニューから 6 桁のパスワードを入れた着用者だけです。**あなたのプロジェクトには
一切触れません。** 保護は SDK が作る一時コピーにだけ適用されるので、コンポーネントを外せば次の
アップロードは普通のアバターに戻り、シーンの側は何が起きたのかすら知りません。

<div align="center">
<img src="https://raw.githubusercontent.com/edonamea/MeshProtect/main/.github/images/locked-ja.png" alt="パスワードがなければアバターは描画されず、入れれば元通りになる" width="92%">
<br><sub>ロック中はそもそも描画されません。崩れた姿が人前に出ることはありません。</sub>
</div>

## 導入

[BOOTH](https://humuhumuhumu.booth.pm/items/8731588) から `.unitypackage` を入手して
インポートするか（無料です）、本リポジトリを UPM / VPM パッケージとして追加してください。

```
https://github.com/edonamea/MeshProtect.git
```

Unity 2022.3 ／ VRChat Avatar SDK3 ／ lilToon 2.x ／ PC 向けアバター
UI は **日本語 / English / 简体中文 / 한국어** に対応し、エディターの言語に合わせて切り替わります。

## 4 ステップ

1. アバターのルートを選択 → `Add Component` → `MeshProtect / Mesh Protect Root`
2. **「パスワードを生成」** を押す（6 桁・各桁 1〜8。自分で 1〜6 桁を入力することもできます）。
   **必ず控えてください** ―― VRChat のパスワード保存は PC ごとなので、別の PC では入れ直しです
3. いつも通り Build & Publish
4. VRChat で Expressions → **Unlock** → 桁ごとに数字を選ぶ

> [!WARNING]
> アップロード前に「このコンポーネントはクライアントで削除されます」という赤いエラーが出ますが、
> これは事実で、無害で、想定どおりです ―― SDK が作る一時コピーからビルド時に取り除かれるためです。
> **Auto Fix だけは絶対に押さないでください。** シーンからコンポーネントごと削除され、
> パスワードも一緒に失われます。

<div align="center">
<img src="https://raw.githubusercontent.com/edonamea/MeshProtect/main/.github/images/inspector.png" alt="Mesh Protect Root のインスペクター" width="60%">
<br><sub>これがインターフェースのすべてです。ベイクボタンはなく、シーンに 2 体目もできず、モデルを編集し直したあとにやり直すものもありません。</sub>
</div>

衣装・PhysBone・Modular Avatar・VRCFury・メッシュ最適化ツールとは、順序を問わず併用できます
（保護はそれら全部の後に走るためです）。Quest 版のアップロードはそのまま素通りするので、
2 つのビルドの間でチェックを付け外しする必要もありません。

## 対応シェーダー

| | |
|---|---|
| **lilToon 2.x** | ネイティブ対応。lilToon 公式の拡張ポイント経由なので、本体をフォークも改変もしておらず、lilToon の更新で壊れることがありません。 |
| **Poiyomi Toon** · **Xiexe's Toon Shader** · **UnityChanToonShader** · **Sunao Shader** · **GTAvaToon** · **blackbody** | 自動グラフト。生成した解錠処理を、そのシェーダーのコピーへパスごとに移植します。テキストを頼りにパッチを当てるのではなく、セマンティクスで位置を特定します。 |
| **lilSSAO** · **lilSSRT** | マージドファミリー。どちらも lilToon のカスタムシェーダーファミリーなので、フォルダーごとアバター用の生成先へ複製し、そこへ解錠処理を統合します。ホストのシェーダーフォルダーの複製が、生成ファミリーと並んでプロジェクトに置かれます。 |

移植を完全にできなかったマテリアルは、壊れる代わりに保護なしで出荷され、Console に名前が出ます。
対応リスト外のシェーダーのものは一切手を触れずそのまま ―― 壊すことはありません。

## 何を止められるのか

| | |
|---|---|
| 抜いたメッシュを Blender で開く | **不可** ―― 先にシェーダーを解析する必要があります |
| ばらして貼り直し、売り直す | **不可** ―― 同上 |
| 既存の解除スクリプトを流す | **不可** ―― アルゴリズムが同じアバターは 2 つとありません |
| DCC ツールでメッシュを再エクスポート | **不可** ―― 頂点の同一性は UV0 の生のビット列です |
| FX コントローラーからアバターの中身を読む | **不可** ―― レイヤー・ステート・ブレンドツリー・クリップは改名済み |
| メッシュ・マテリアル・オブジェクトの名前を読む | **不可** ―― そのアバター自身のアルゴリズムから生成された名前です |
| このアバターのシェーダーを手で解析する | 数時間かかり、**しかも次のアバターには何の役にも立ちません** |

目指したのは「破られないこと」ではありません。**破ることが割に合わなくなること**です。コストは
モデル 1 体ごとに丸ごと払い直しになり、それが、実際にモデルが出回ったときに起きることの大半 ――
スクリプト化された、オフラインの、大量処理の攻撃 ―― を取り除きます。

テクスチャは保護されません。着用中の復元済みメッシュを同じインスタンスから GPU 経由で
キャプチャすることも防げません。この 2 つの限界とその理由は
[技術資料.txt](技術資料.txt) に書いてあります ―― 販売物に使う前に読んでおいてください。

## 自分の仕事を自分で検証します

この分野のツールは静かに失敗し、気づくのはゲームの中、というのが通例です。これはアップロードが
終わる前に検証します。生成シェーダーを C# の暗号処理と **GPU 上で** 突き合わせ、焼いたメッシュ
すべてをブレンドシェイプのフレームを含めて頂点単位で復元・照合し、解錠メニューとその転送ビットが
揃っていることを確認し、さらに全コンポーネントの全シリアライズフィールドを歩いて、元のメッシュが
どこからも到達できないことを証明します。完全にできなかったものは手を触れずに Console で名指し
されるので、ビルドが黙って壊れたアバターを出荷することはありません。

## 仕組み

- **変位は保存せず、生成します。** 各頂点は、パスワードとその頂点自身の同一性から導いたハッシュ
  の値だけ、接線と法線の方向へ押し出されます。読み戻せる係数はメッシュのどこにもありません。
- **アルゴリズムはアバターごとに違います。** パスワードを設定すると新しいハッシュプログラムが
  組み立てられ、定数を焼き込んだシェーダーファミリーが丸ごと生成されます。プロパティ名・
  パラメータ名・アセット名も生成なので、アップロードされたアバターはこのツールの名前すら含みません。
- **ロック中のアバターは「爆発」ではなく不可視です。** 全頂点が 1 点に潰れ、どのパスでも
  ラスタライズされません。パスワードが違うときも同じく潰れます ―― 正しく見えるか、いないか、
  どちらかであって、崩れた姿が人前に出ることはありません。
- **すべて整数演算**なので、C# と HLSL はどの GPU でもビット単位で一致します。

**動作コスト:** UV チャンネル 1 つ（TEXCOORD6）／ Expression パラメータ 24 bit ／ 頂点シェーダー
命令が数個で、**アバターの Performance Rank は変わりません** ／ アルゴリズム生成時に一度だけ
約 3.5 秒。パスワードの変更にも再アップロードにも追加コストはかかりません。

## さらに詳しく

📄 **[技術資料.txt](技術資料.txt)** ―― 技術的な詳細、保護の範囲、パネルの項目ごとの説明、トラブル対応
📘 **[お読みください.txt](お読みください.txt)** ―― 同梱の説明書
📋 **[CHANGELOG.md](CHANGELOG.md)** ―― 更新履歴
💬 ご質問・不具合報告 ―― [BOOTH の商品ページ](https://humuhumuhumu.booth.pm/items/8731588)のお問い合わせから

## ライセンス

[MIT ライセンス](LICENSE)です。ツール本体を含め、使用・改変・再配布・販売はすべて自由です
（複製物には著作権表示と許諾表示を残してください）。このツールで作ったものの販売も自由です。

`Shaders/Templates` の lilToon テンプレートは
[lilxyzw/lilToon](https://github.com/lilxyzw/lilToon)（MIT）由来で、そのライセンスに従います。
変位の手法は [rygo6/GTAvaCrypt](https://github.com/rygo6/GTAvaCrypt) と
[lilxyzw/AvaterEncryption](https://github.com/lilxyzw/AvaterEncryption)（いずれも MIT）の
先行研究を踏まえています。帰属の全文は [NOTICE.md](NOTICE.md) にあります。
