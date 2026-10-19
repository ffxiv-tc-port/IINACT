using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;

namespace RainbowMage.OverlayPlugin
{
    // 把「功能被停用」這類訊息印到聊天視窗，免得只寫在 log 裡沒人看到。
    // 契約：Post 可從任何執行緒呼叫；同一個 key 在插件這次載入期間只印一次；
    // 一律等登入（本機角色存在）後在框架執行緒上印，登入前排著。
    public sealed class ChatEcho : IDisposable
    {
        private readonly IChatGui chatGui;
        private readonly IFramework framework;
        private readonly IClientState clientState;
        private readonly IObjectTable objectTable;

        private readonly object gate = new object();
        private readonly HashSet<string> postedKeys = new HashSet<string>();
        private readonly List<(string Message, bool IsError)> pending = new List<(string, bool)>();
        private bool subscribed;
        private bool disposed;

        public ChatEcho(IChatGui chatGui, IFramework framework, IClientState clientState, IObjectTable objectTable)
        {
            this.chatGui = chatGui;
            this.framework = framework;
            this.clientState = clientState;
            this.objectTable = objectTable;
        }

        public void Post(string key, string message, bool isError)
        {
            lock (gate)
            {
                if (disposed || !postedKeys.Add(key))
                    return;

                pending.Add((message, isError));
                if (subscribed)
                    return;

                // 掛／拿 Update 都在鎖內做，subscribed 才會與實際掛著的次數一致。
                subscribed = true;
                framework.Update += OnUpdate;
            }
        }

        // 框架執行緒；只在有待印訊息時掛著，印完就拿掉。
        private void OnUpdate(IFramework _)
        {
            if (!clientState.IsLoggedIn || objectTable.LocalPlayer == null)
                return;

            List<(string Message, bool IsError)> batch;
            lock (gate)
            {
                batch = new List<(string, bool)>(pending);
                pending.Clear();
                subscribed = false;
                framework.Update -= OnUpdate;
            }

            foreach (var (message, isError) in batch)
            {
                if (isError)
                    chatGui.PrintError(message);
                else
                    chatGui.Print(message);
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                pending.Clear();
                subscribed = false;
                framework.Update -= OnUpdate;
            }
        }
    }
}
