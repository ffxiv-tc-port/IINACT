# IINACT TC (Traditional Chinese) 版本更新指南

本文件說明在 IINACT upstream 發布新版本（例如升級至 Dalamud API 13 + .NET 10）時，
維護 TC fork 所需確認的完整步驟。

---

## 一、更新策略

TC fork 由兩個子專案組成，各自維護：

```
你的 machina fork (PlusoneChiang/machina)
    ├── upstream (ravahn/machina)  ← TC/KR/Global opcodes 來源
    └── xom (marzent/machina)     ← Global Dalamud + 建置系統

你的 IINACT fork (PlusoneChiang/IINACT)
    ├── upstream IINACT            ← 最新框架與功能
    └── machina submodule          ← 指向上面的 machina fork
```

### 完整更新流程：

```
【machina 先更新】
git fetch upstream + git fetch xom
    ↓
merge upstream/master（TC/KR/Global opcodes）
    ↓
merge xom/dalamud（若有 Dalamud 建置更新）
    ↓
降版 machina runtime（若需要）
    ↓
push 到 PlusoneChiang/machina

【IINACT 再更新】
取得最新 upstream IINACT
    ↓
降版 IINACT 框架與 runtime（若 TC Dalamud 尚未跟進）
    ↓
還原 TC 專屬修改（本文件 Step 3）
    ↓
更新 machina submodule 指向新版
    ↓
建置 & 測試
```

---

## 二、框架降版（從 API 14/Net10 → API 12/Net9）

### 2-1. .NET Runtime 降版

修改以下所有 `.csproj` 的 `<TargetFramework>`：

| 專案 | 路徑 | 改為 |
|---|---|---|
| IINACT | `IINACT/IINACT.csproj` | `net9.0-windows` |
| OverlayPlugin.Core | `OverlayPlugin.Core/OverlayPlugin.Core.csproj` | `net9.0-windows` |
| OverlayPlugin.Common | `OverlayPlugin.Common/OverlayPlugin.Common.csproj` | `net9.0-windows` |
| NotACT | `NotACT/NotACT.csproj` | `net9.0-windows` |
| FetchDependencies | `FetchDependencies/FetchDependencies.csproj` | `net9.0-windows` |
| Machina | `machina/Machina/Machina.csproj` | `net9.0` |
| Machina.FFXIV | `machina/Machina.FFXIV/Machina.FFXIV.csproj` | `net9.0` |

> **注意**：Test 專案（`Machina.Tests`、`Machina.FFXIV.Tests`）不影響發布，可保持較新版本。

### 2-2. Dalamud API 版本

修改 `repo.json`：

```json
"DalamudApiLevel": 12
```

確認 `DalamudLibPath` 指向的 Dalamud dev 目錄使用正確的 API 12 版本（TC Dalamud fork）。

---

## 三、TC 專屬程式碼修改（每次 upstream 更新後必須確認）

### 3-1. `IINACT/Plugin.cs` — 強制 TC 地區

```csharp
// 找到這行，確認存在：
OpcodeManager.Instance.SetRegion(GameRegion.TraditionalChinese);
```

若被 upstream 覆蓋，在 Plugin constructor 的 `Log = pluginLog;` 後一行加回。

### 3-2. `IINACT/FfxivActPluginWrapper.cs` — 地區與語言設定

確認以下兩處：

**語言映射**（`ClientLanguage` property，約第 136 行）：

```csharp
private Language ClientLanguage =>
    dalamudClientLanguage switch
    {
        Dalamud.Game.ClientLanguage.Japanese => Language.Japanese,
        Dalamud.Game.ClientLanguage.English  => Language.English,
        Dalamud.Game.ClientLanguage.German   => Language.German,
        Dalamud.Game.ClientLanguage.French   => Language.French,
        _ when dalamudClientLanguage.ToString() == "ChineseSimplified"  => Language.Chinese,
        _ when dalamudClientLanguage.ToString() == "TraditionalChinese" => Language.TraditionalChinese,
        _ => Language.English
    };
```

> **為何用 `.ToString()` 比較**：TC Dalamud fork 有 `ClientLanguage.SimplifiedChinese` 和 `ClientLanguage.TraditionalChinese`，
> 標準 Dalamud SDK 無這些值，用字串比較可避免編譯期的 enum 衝突。

**地區設定**（`SetupSettingsMediator` 方法，約第 165 行）：

```csharp
RegionID = Region.TraditionalChinese,
```

### 3-3. `IINACT/Network/ZoneDownHookManager.cs` — 核心封包解混淆

> 🔴 **2026-08-13 訂正**：本節舊版寫「TC binary 沒有靜態 `OpcodeKeyTable`，全域簽章
> `OpcodeKeyTableSignature` 不存在」，**兩句都是錯的**。照著它做出來的「把 Key0/Key1/Key2
> 當成三筆 opcodeKeyTable」實作會讓所有傷害數值被 XOR 成六萬多、DPS 差兩個數量級。
> 下面是離線反組譯 ＋ 實機封包向量驗證之後的正確版本。

**台服 `PacketDispatcher::OnReceivePacket` 與國際服 7.3 世代同構**（7.20、7.25 皆然）：

```
baseKey        = Key[opcode % 3] - GameSessionRandom - LastPacketRandom
opcodeBasedKey = opcodeKeyTable[(opcode + baseKey) % N]      // N＝表長，每版不同
ActionEffect   : *(int*)(ipc + 24)         -= baseKey
                 *(short*)(ipc + 64 + i*8) ^= (short)(baseKey + opcodeBasedKey)
```

- `opcodeKeyTable` 是 **`.rdata` 裡的 int32 靜態表**，長度＝取模數 N：7.20 是 96（`0x21A4510`）、
  **7.25 是 233**（`0x21B7BA0`，指令改成 `imul eax, eax, 0E9h`）。Unscrambler73 用 `% 表長`，所以表長必須精確。
- **IINACT 原有的 `OpcodeKeyTableSignature`（`?? ?? ?? 2B C8 ?? 8B ?? 8A ?? ?? ?? ?? 41 81`）
  在台服照樣命中**。⇒ **位址不要寫死**，走簽章掃描。
- 台服要用 **`Unscrambler73`**，不是 `Unscrambler72`（7.2 世代把 `opcodeBasedKey` 寫死成每個 opcode
  一個立即數，台服沒有那組常數）。

**Constructor 中的 TC 路徑**（2026-09-29 起）：

- 解混淆 opcode、金鑰初始化 opcode、取模數**逐遊戲版本**寫在 `TcObfuscationTables`
  （鍵＝`ffxivgame.ver` 全字串）。改版後新增一筆，舊版本的項目刪掉即可。
- 版本不在表上 ⇒ **整個不解混淆**＋Warning＋遊戲內通知（用錯版本的 opcode 會把別種封包 XOR 壞，比不解更糟）。
- 掃到的表長 ≠ 該版取模數 ⇒ 同樣不解混淆（fail-closed）。
- Machina 表與執行檔推導值不同時，解混淆以推導值為準並寫 Warning（代表 machina 子模組的表過期）。

**`UpdateKeys()` 不碰 `opcodeKeyTable`**：三把 session 金鑰與靜態表是兩件不同的東西。

**驗證工具**：
- `~/.claude/tools/sigscan/iinact_tc_opcodes_derive.py` —— 新版本一律先跑這支
  （見 4-3 節）；它同時印出取模數與表位址。
- `~/.claude/tools/sigscan/iinact_tc_unscramble_verify.py` —— 7.20 的實機封包向量重放
  （向量綁 7.20 的表，7.25 起只剩歷史意義）。

---

## 四、TC Opcode 更新（遊戲版本更新時）

**檔案**：`machina/Machina.FFXIV/Headers/Opcodes/TraditionalChinese.txt`

TC opcodes 由 **ravahn (`upstream/master`)** 維護，與 Korean opcodes 一同更新。
每次 TC 遊戲版本更新後需更新此檔案。

### 4-1. Machina 三個 Remote 的責任

| Remote | 來源 | 維護內容 |
|---|---|---|
| `upstream` | ravahn/machina | `TraditionalChinese.txt` + `Korean.txt` + `Global.txt` + struct 更新 |
| `xom` | marzent/machina | `Global.txt` Dalamud 版本 + 建置系統（runtime 設定） |
| `origin` | PlusoneChiang/machina | TC branch = merge 以上兩者 + runtime 降版 |

### 4-2. Machina 更新步驟

```bash
cd machina

# 取得最新版本
git fetch upstream   # TC/KR/Global opcodes 來源（最關鍵）
git fetch xom        # Dalamud 建置系統更新

# 合併 upstream（TC opcodes 在這裡，配合 Korean 一起更新）
git merge upstream/master

# 合併 xom（若有 Dalamud/建置相關更新）
git merge xom/dalamud

# 若新版 runtime 升到 net10，降回 net9
# 修改 Machina.FFXIV/Machina.FFXIV.csproj：net10.0 → net9.0
# 修改 Machina/Machina.csproj：net10.0 → net9.0

# push 更新
git push origin tc/net9

# 回到 IINACT，更新 submodule 指向
cd ..
git submodule update --remote machina
# 或手動 cd machina && git checkout <new_commit>
```

### 4-3. 新版本的 opcode：離線從執行檔推（不必等上游，也不必看封包）

```bash
python ~/.claude/tools/sigscan/iinact_tc_opcodes_derive.py
```

它把四條互相獨立的證據都印出來：①`OnReceivePacket` 解混淆分支動到的欄位偏移 vs Unscrambler73 各型別
②主分派跳表每個 case 的「錄製函式」寫死的 payload 大小 ③內部追蹤 switch 的 vtable 槽序（BossMod OpcodeMap
的做法）對 BossMod `ServerIPC.cs` PacketID 的局部位移 ④用大小過濾候選。
輸出分三段：A＝解混淆 opcode（直接填 `TcObfuscationTables`）、D＝Machina `TraditionalChinese.txt` 與
OverlayPlugin `opcodes.jsonc` 的建議值。**`AUTO*` 列可直接採用；`MANUAL` 列要自己反組譯處理函式確認**
（7.25 時只剩 CEDirector 認不出來，所以 7.25 的 jsonc 刻意不放它）。
`--expect-725` 是回歸測試：改工具後拿 7.25 執行檔跑，必須 PASS。

7.25（`2026.09.14.0000.0000`）的結果（2026-09-29 逐支人工確認，推導細節見 IINACT 該次 commit 訊息）：

| 名稱 | 7.25 | 名稱 | 7.25 |
|---|---|---|---|
| StatusEffectList | 0x300 | ActorControl | 0x325 |
| StatusEffectList2（博茲雅變體） | 0x352 | ActorControlSelf | 0x111 |
| StatusEffectList3 | 0x16E | ActorControlTarget | 0x25A |
| BossStatusEffectList | 0x1EC | UpdateHpMpTp | 0x38F |
| StatusEffectListForay3（新月島） | 0x19D | PlayerSpawn | 0x37C |
| Ability1 / 8 / 16 | 0x1BE / 0x13C / 0x133 | NpcSpawn / NpcSpawn2 | 0x374 / 0x2D6 |
| Ability24 / 32 | 0x07A / 0x1DF | ActorMove / ActorSetPos | 0x229 / 0x279 |
| ActorCast | 0x35D | ActorGauge | 0x0FD |
| EffectResult / EffectResultBasic | 0x311 / 0x162 | PresetWaymark / Waymark | 0x1C9 / 0x338 |
| SystemLogMessage | 0x2CF | | |

只在解混淆表出現的：ActionEffect02 0x336、ActionEffect04 0x335、Examine 0x1E1、UpdateGearset 0x140、
UpdateParty 0x30C、UnknownEffect01 0x365、UnknownEffect16 0x0C3；金鑰初始化 opcode 0x0EB / 0x345、模式位元組 0x97。

> ⚠️ NpcSpawn / NpcSpawn2 的解混淆分支位元組相同；名字靠 payload 大小分（0x290 → NpcSpawn、0x400 → NpcSpawn2）。
> 同理 EffectResult 1/4/8/16、SystemLogMessage 1/2/4/8/16 共用同一個 case 本體，只能靠大小分辨，
> Machina 要的是最小的那個變體。

### 上游來源（僅供對照）

1. ravahn/machina 也維護 TraditionalChinese.txt（7.20 在台服改版後約 6~7 天才更新）
2. FFXIVOpcodes 或 TC 社群的分析

---

## 五、Binary 簽章分析（opcodes 以外的底層問題）

若 TC 遊戲大改版（major patch）後 plugin 崩潰，需重新確認：

### 5-1. `GenericDownSignature` 是否仍有效

**簽章**：`E8 ?? ?? ?? ?? 4C 8B 4F 10 8B 47 1C 45`

用 binary 搜尋工具（如 010 Editor 或 Python）在新版 `ffxiv_dx11.exe` 確認仍有 **3 個** match。

```python
import re
with open("ffxiv_dx11.exe", "rb") as f:
    data = f.read()
pattern = bytes.fromhex("4C8B4F108B471C45")
matches = [m.start() for m in re.finditer(re.escape(pattern), data)]
print(f"找到 {len(matches)} 個 match")  # 預期 3 個，ZoneDownHook 用 index [2]
```

> ⚠️ 3 個命中呼叫的是**三個不同**的函式（同一份樣板的三種連線），不是呼叫點收斂；
> IINACT 靠位址順序取第 3 個＝區域連線。7.25 仍成立（實機 log 裡有只會走區域連線的封包進來），
> 但這是「順序剛好沒變」，改版後要重看：hook 錯連線的症狀是 ACT 完全沒有戰鬥行、也沒有任何錯誤。

### 5-2. `PacketDispatcher` vtable 位移是否仍正確

TC 的 `PacketDispatcher` struct 關鍵欄位（FFXIVClientStructs 定義）：

| 欄位 | Offset | 用途 |
|---|---|---|
| Key0 | 0x20 | TC key table[0] |
| Key1 | 0x24 | TC key table[1] |
| Key2 | 0x28 | TC key table[2] |
| GameRandom | 0x18 | 解混淆計算 |
| LastPacketRandom | 0x1C | 解混淆計算 |

若 FFXIV 大版本更新更動了 CharacterStruct layout，需重新確認這些 offset。

---

## 六、建置與驗證

```bash
# 建置
dotnet build IINACT/IINACT.csproj -c Release

# 預期結果：0 errors（3 個 pre-existing warnings 可忽略）
```

啟動後在 Dalamud log 中確認以下訊息：

```
[ZoneDownHookManager] 台服 2026.09.14.0000.0000：opcodeKeyTable 模組位移 0x21B7BA0、233 筆 int32（與這個版本的取模數一致）
[效果解密診斷] 監看中：ActionEffect01=0x01BE、…
[效果解密診斷] 判定：未用槽 N 個解密後全為 0 ✅ 解密正確（…）      ← 第一次有 ActionEffect 時出現
```

不應出現：`不在本版 IINACT 的解混淆表內`（版本沒支援）、`Machina 台服表寫 0x…`（machina 子模組的表過期）、
`🔴 殘留`（解混淆常數對不上）、OverlayPlugin 的 `找不到對應的 resource opcode`（opcodes.jsonc 沒這個版本）。

```
```

---

## 七、已知限制與待確認事項

| 項目 | 狀態 | 說明 |
|---|---|---|
| 解混淆正確性 | ✅ 2026-08-13 已驗證 | 台服＝Unscrambler73 演算法 ＋ `.rdata` 靜態 `opcodeKeyTable`（簽章掃描取址）。10 筆實機封包向量重放通過：未用效果槽解密後全為 0、型別 0x1B StartActionCombo 的值等於解出來的 actionId。工具見 3-3 節 |
| 7.25 opcode | ⚠️ 離線推導、待實機 ✅ | 2026-09-29 由執行檔離線推導（4-3 節）；ActionEffect 解密要等實機出現 `✅ 解密正確` 才算閉環 |
| OverlayPlugin CEDirector（7.25） | ⚠️ 未辨識 | 7.25 的 `opcodes.jsonc` 刻意不放，只有這條自訂 log line 停用 |
| PacketDispatcher 初始化時序 | ⚠️ 未驗證 | Plugin 載入早期 `GetInstance()` 可能回傳 null |
| TC locale 在 cactbot overlay | ℹ️ 設計決策 | TC 沒有獨立 locale → fallback 到 English intl（`FFXIVProcessIntl` offset 正確） |
| FetchDependencies CN URL | ℹ️ 無需修改 | TC 使用 Global FFXIV_ACT_Plugin（iinact.com），非 cninact.diemoe.net |

---

## 八、快速 Checklist

### Machina 更新

- [ ] `git fetch upstream && git fetch xom`
- [ ] `git merge upstream/master`（TC opcodes 在這裡）
- [ ] `git merge xom/dalamud`（若有 Dalamud 相關更新）
- [ ] 若 runtime 升版：`Machina.FFXIV.csproj` + `Machina.csproj` 降回 `net9.0`
- [ ] `git push origin tc/net9`

### IINACT 更新

每次 upstream IINACT 更新後，依序確認：

- [ ] `git submodule update --remote machina`（或手動指向新 machina commit）
- [ ] `repo.json`：`DalamudApiLevel` = 12
- [ ] 所有 `.csproj`：`TargetFramework` = `net9.0-windows` / `net9.0`
- [ ] `Plugin.cs`：`SetRegion(GameRegion.TraditionalChinese)` 存在
- [ ] `FfxivActPluginWrapper.cs`：`ClientLanguage` switch 有 `TraditionalChinese` case
- [ ] `FfxivActPluginWrapper.cs`：`RegionID = Region.TraditionalChinese`
- [ ] `ZoneDownHookManager.cs`：`TcObfuscationTables` 有目前遊戲版本那一筆（`iinact_tc_opcodes_derive.py` 的 A 段）
- [ ] `TraditionalChinese.txt`：若遊戲有更版，換成同一支工具 D 段的值（machina 子模組先 commit，再更新 IINACT 的 gitlink）
- [ ] `OverlayPlugin.Core/resources/opcodes.jsonc`：TraditionalChinese 換成新版本字串與新值
- [ ] `dotnet build` 0 errors
- [ ] 遊戲啟動後 log 顯示 TC 初始化訊息
