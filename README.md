# IINACT

[IINACT](https://github.com/marzent/IINACT) 的台服移植版：在 Dalamud 內建置一個相容 ACT 的 WebSocket 資料伺服器，讀取戰鬥事件資料供疊加層與紀錄工具使用。**本身不繪製任何疊加層畫面。**

## 功能

- **內建 Overlay Plugin**（現代 .NET 移植版）：提供標準 ACT/OverlayPlugin 相容的 WebSocket 服務，可搭配 [Kagerou](https://plusonechiang.github.io/kagerou/overlay/) 等疊加層使用
- **資料來源**：純讀取遊戲已解析封包，不需要額外注入或提升權限的封包截取
- **紀錄輸出與 FFLogs Uploader 相容**：紀錄檔存放於「文件」資料夾下的 `IINACT`
- **台服支援**：Region 固定為 TraditionalChinese、封包金鑰處理已針對台服執行檔調整、預設疊加層清單指向台服在地化的 Kagerou fork

疊加層畫面需另外安裝 [Browsingway](https://github.com/Styr1x/Browsingway)、[Next UI](https://github.com/kaminaris/Next-UI) 等瀏覽器渲染插件顯示。

## 授權

依照原專案授權條款發布，詳見 [LICENSE](LICENSE)。
