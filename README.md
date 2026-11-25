# IINACT

[IINACT](https://github.com/marzent/IINACT) 的台服移植版：在 Dalamud 內執行 FFXIV_ACT_Plugin 解析戰鬥數據，並提供相容 ACT／OverlayPlugin 的 WebSocket 資料伺服器，給疊加層與紀錄工具使用，不需要另外安裝 ACT。**本身不繪製任何疊加層畫面。**

## 功能

- **內建 Overlay Plugin**（現代 .NET 移植版）：提供標準 ACT／OverlayPlugin 相容的 WebSocket 服務，可搭配 [Kagerou](https://plusonechiang.github.io/kagerou/overlay/) 等疊加層使用；主視窗可產生疊加層網址
- **資料來源**：掛在遊戲的封包接收函式上，先讓遊戲照常處理，再讀取收到的網路資料交給解析元件（只讀，不修改、不送出封包）
- **解析元件 FFXIV_ACT_Plugin**：第一次載入與有新版時自動下載，只安裝 SHA-256 在已知清單裡的版本；新版不在清單時不安裝、沿用目前的版本並跳通知，本機沒有可用版本時改裝清單裡的已知版本；都拿不到時插件不載入並說明原因
- **紀錄輸出與 FFLogs Uploader 相容**：紀錄檔預設存放於「文件」資料夾下的 `IINACT`，可設定自動刪除舊紀錄、PvP 時不寫紀錄
- **文字轉語音**：給疊加層的語音提示使用 Windows 內建語音（SAPI）；可選擇內建語音不能用時改用 Google 語音（預設關），或一律使用 Google 語音
- **台服支援**：Region 固定為 TraditionalChinese、封包 opcode 與解混淆表針對台服執行檔調整、預設疊加層清單指向台服在地化的 Kagerou fork
- 提供 IPC（`IINACT.CreateSubscriber` 等）給其他插件訂閱資料

## 指令

- `/iinact`：開啟主視窗
- `/endenc`：結束目前正在解析的戰鬥

## 台服版的修改

- 封包 opcode、解混淆表與倒數計時、MapEffect 等自訂 log line 對應台服版本；遇到還沒支援的遊戲版本時停用解混淆並通知
- 解析元件內建的名稱表跟不上台服改版時，載入時以遊戲資料補上缺的技能、狀態與區域名稱，新內容不再顯示成 unknown
- cactbot 疊加層使用繁體中文語系；IINACT 重新啟動或更新後，不必換區就能拿到目前的區域、角色與戰鬥狀態
- 解析元件只安裝已知雜湊的版本，換版期間沿用舊版並提示
- 遊戲改版後封包接收函式的特徵碼對不上時，插件照常載入並明講「收不到網路資料，戰鬥解析停用」；命中數不對時不掛 hook
- Windows 內建語音不能用時，不再自動把文字送給 Google
- 疊加層設定改成整份寫好才替換原檔，存檔途中出錯不會把設定檔寫壞；存檔失敗只寫進紀錄，不再跳出對話框卡住遊戲
- 其他插件透過 IPC 訂閱事件時，改在遊戲主執行緒依序送出
- 修正 WebSocket Server 分頁每幀存檔造成的卡頓，多處降低記憶體用量
- 介面繁體中文化

## 安裝

1. 在遊戲內輸入 `/xlsettings` 開啟「Dalamud 設定」，到「試驗性功能」分頁的「第三方插件倉庫」加入
   `https://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json`
   並啟用、儲存。
2. 輸入 `/xlplugins` 開啟「插件管理員」，搜尋「IINACT」安裝。

同一個插件只裝這裡的版本，不要同時裝上游版。

## 需要搭配的插件

- 顯示疊加層畫面：[Crossingway](https://github.com/ffxiv-tc-port/Crossingway)（遊戲內瀏覽器覆蓋層）

授權：GPL-3.0（見 LICENSE）
