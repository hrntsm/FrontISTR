# トポロジー最適化（コンプライアンス最小化）の実装計画

## Context

FrontISTRでSIMP法によるコンプライアンス最小化トポロジー最適化を実現する。
外部 **C# プログラム** が材料定数を操作する形で実装する。

---

## 技術的確認

### コンプライアンス最小化が自己随伴である理由

目的関数 C = Fᵀu の設計感度（SIMP法）：

```
dC/dρ_e = -p · ρ_e^(p-1) · u_eᵀ K_0_e u_e
```

- K が対称なので随伴問題 Kᵀλ = ∂C/∂u = F は順解析と同一
- λ = u（随伴変数 = 変位）が成立
- **感度 = 要素ひずみエネルギー** W_e = (1/2) u_eᵀ K_e u_e のみから計算可能

→ **随伴ソルバーの FrontISTR への実装は不要** ✓

要素感度の簡略式：
```
s_e = -p · W_e / ρ_e
    = -p · (σ_e:ε_e · V_e / 2) / ρ_e
```

FrontISTR の結果ファイル (.res) に含まれる要素応力・ひずみから直接計算可能。

### 外部スクリプト方式の実現性

**可能だが、FrontISTR の材料モデルの制約に注意が必要。**

FrontISTR は `!SECTION, TYPE=SOLID, EGRP=<グループ名>, MATERIAL=<材料名>` でマテリアルを**要素グループ単位**で割り当てる。
トポロジー最適化で要素ごとにEを変えるには、各要素を個別のグループにする必要がある。

- 小規模問題（要素数 ~1000 程度）: 要素ごとのグループ定義で対応可能
- 大規模問題: FrontISTR 内部で密度場を読む機能が必要（別途検討）

---

## 実装方針

**FrontISTR ソースコードの変更は行わない**。
外部 **C# コンソールアプリ** で最適化ループを構成する。

### 全体フロー

```
[1] 前処理: メッシュの要素ごとにグループを作成 (.msh)
         ↓
[最適化ループ]
[2] SIMP で E_e = E_min + ρ_e^p · E_0 を計算
[3] .cnt ファイルに材料定数を書き込み
[4] FrontISTR 実行 (Process.Start) → 変位 / 応力 / ひずみを計算
[5] 結果ファイル (.res.0.0) から要素応力・ひずみを読み取り
[6] 感度 s_e = -p · (σ:ε · V_e / 2) / ρ_e を計算
[7] 感度フィルタ（重み付き平均）
[8] OC 法で密度更新（体積制約のもとで二分法）
[9] 収束判定 → 収束したら終了、しなければ [2] へ
```

---

## プロジェクト構成

```
tools/topopt/
├── TopOpt.sln
└── TopOpt/
    ├── TopOpt.csproj          (.NET 8, コンソールアプリ)
    ├── Program.cs             エントリーポイント・ループ制御
    ├── Simp.cs                感度計算・OC密度更新
    ├── FistrWriter.cs         .cnt ファイルの材料定数書き込み
    ├── ResReader.cs           FrontISTR 結果ファイル (.res) の読み取り
    └── MeshPreprocessor.cs    .msh 要素グループの自動生成

tutorial/topopt/
├── cantilever.msh             サンプル：片持ち梁メッシュ（要素グループ済み）
└── cantilever.cnt             サンプル：片持ち梁制御ファイル
```

---

## 各クラスの詳細

### `ResReader.cs`
FrontISTR のネイティブ結果ファイル（`.res.0.0`）を直接読む。**ASCII テキスト**なので XML 不要。

フォーマット確認済み（`tests/analysis/static/exB/B341.res.0.0`, `hecmw_result_io_txt.c`）：

```
*fstrresult 2.0
*comment
static_result
*global
 1
 1
TOTALTIME
 0.0...
*data
 99 240          ← n_node, n_elem
 3 2             ← n_node_component, n_elem_component
 3 6 1 6 6      ← DOF/component: DISP(3) NSTRESS(6) NMISES(1) ESTRAIN(6) ESTRESS(6)
DISPLACEMENT
NodalSTRESS
NodalMISES
ElementalSTRAIN
ElementalSTRESS
1001             ← node global ID
 0.0 ...         ← 10値 (3+6+1)
...              ← 全節点
1                ← element global ID
 0.0 ...         ← 12値 (6+6)
...              ← 全要素
```

要素応力・ひずみを .res に出力するには `.cnt` に以下を追加：
```
!OUTPUT_RES
 ESTRESS, ON
 ESTRAIN, ON
```
（VTK 出力は**不要**。`!WRITE,RESULT` だけで十分）

主なメソッド：
```csharp
ResResult Read(string resPath);
// ResResult.ElemStress[elemIdx, 6], ElemStrain[elemIdx, 6]
// ResResult.ElemIds[elemIdx] ← global ID との対応
```

### `FistrWriter.cs`
`.cnt` に各要素グループの材料定義を書き出す（イテレーションごとに再生成）。

```
!MATERIAL, NAME=e_1
!ELASTIC
 210000.0, 0.3
!MATERIAL, NAME=e_2
!ELASTIC
 87420.0, 0.3
...
```

E 値は `E_e = E_min + rho_e^p * E_0` で計算。

### `MeshPreprocessor.cs`
既存 .msh を読み込み、設計領域の各要素を個別 EGRP に変換して新 .msh を生成。
（一度だけ実行。最適化ループ中は .msh は変更しない）

```
# 変換前
!SECTION, TYPE=SOLID, EGRP=ALL, MATERIAL=M1

# 変換後
!SECTION, TYPE=SOLID, EGRP=e_1, MATERIAL=e_1
!SECTION, TYPE=SOLID, EGRP=e_2, MATERIAL=e_2
...
```

### `Simp.cs`
感度はひずみエネルギー密度から計算（Voigt記法でのドット積）：

```
W_e = 0.5 * dot(σ_e, ε_e) * V_e
s_e = -p * W_e / rho_e
```

主なメソッド：
```csharp
double[] ComputeSensitivity(double[,] stress, double[,] strain,
                             double[] volume, double[] rho, double p);
double[] ApplyFilter(double[] sensitivity, double[,] coords, double rMin);
// 重み付き平均: w_ij = max(0, rMin - dist(i,j)) / Σw_ij
double[] OcUpdate(double[] rho, double[] sensitivity, double volFrac,
                  double delta = 0.2);
// 二分法で Lagrange乗数 λ を求め ρ_e *= sqrt(-s_e / λ) を適用
```

### `Program.cs`
コマンドライン引数: `--mesh <path>`, `--cnt <path>`, `--volfrac <f>`, `--penal <p>`, `--rmin <r>`, `--maxiter <n>`, `--fistr <path>`

```
各イテレーション:
  1. FistrWriter.WriteMaterials(rho[], E0, Emin, nu, p)     → .cnt 更新
  2. Process.Start(fistrPath, args).WaitForExit()            → FrontISTR 実行
  3. ResReader.Read("problem.res.0.0")                       → stress[ne,6], strain[ne,6]
  4. Simp.ComputeSensitivity(stress, strain, vol, rho, p)   → s[]
  5. Simp.ApplyFilter(s, elemCentroids, rMin)               → sf[]
  6. rho = Simp.OcUpdate(rho, sf, volFrac)
  7. 収束判定: max(|rho_new - rho_old|) < 1e-3 → 終了
  8. コンプライアンス C を表示してループ継続
```

---

## 利用できる既存 FrontISTR 資産

| ファイル | 参照目的 |
|---------|---------|
| `fistr1/src/lib/physics/material.f90:229` | 材料変数のインデックス確認（M_YOUNGS=1 等）|
| `tests/analysis/static/exB/B341.msh` | .msh 形式のリファレンス |
| `tests/analysis/static/exB/B341.cnt` | .cnt 形式のリファレンス |

---

## 検証方法

1. **片持ち梁問題（小規模）**:
   - 要素数 32×20 = 640 要素程度
   - 体積率 50%、先端に集中力
   - 期待結果：対角方向のトラス状トポロジー
2. **収束確認**: 各イテレーションで C（コンプライアンス）が単調減少
3. **体積制約確認**: Σρ_e · V_e / V_total ≈ vol_frac を満足

---

## 補足：大規模問題への拡張（本計画外）

要素数が多い場合は FrontISTR 本体に密度場ファイル読み込み機能を追加する方が現実的。
その際も随伴ソルバーは不要で、C# プログラムは密度更新のみ担当する形になる。
