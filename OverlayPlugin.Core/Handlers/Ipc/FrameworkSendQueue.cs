#nullable enable
using System;
using System.Collections.Concurrent;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace RainbowMage.OverlayPlugin.Handlers.Ipc;

// 契約：任何執行緒都可呼叫；依 Post 的先後送出、一筆不漏（Close 之前收下的都會送），同一時間只送一筆。
// 背景執行緒 Post 的一律排到框架執行緒送（插件自己的執行緒不呼叫別的插件）；框架執行緒上 Post 就地送。
// PostAndFlush／Close 只用在對方插件呼叫進來的那一次呼叫裡，就地送（跑在對方自己的執行緒上）。
internal sealed class FrameworkSendQueue
{
    private readonly ConcurrentQueue<JObject> pending = new();
    private readonly object drainLock = new();
    private readonly Action<JObject> send;
    private readonly Action<Action> runOnFramework;
    private readonly Func<bool> isFrameworkThread;
    private readonly Action<Exception> onError;
    private int drainScheduled;
    private volatile bool closed;

    public FrameworkSendQueue(Action<JObject> send, Action<Action> runOnFramework, Func<bool> isFrameworkThread,
                              Action<Exception> onError)
    {
        this.send = send;
        this.runOnFramework = runOnFramework;
        this.isFrameworkThread = isFrameworkThread;
        this.onError = onError;
    }

    // 拍快照：派送端把同一個 JObject 交給每個接收者，送出前可能已被別人改過。
    // 只排「排空佇列」、不是每筆排一個工作：框架的工作排程器不保證執行順序，順序由這個佇列保。
    public void Post(JObject e)
    {
        if (closed) return;
        pending.Enqueue((JObject)e.DeepClone());
        if (isFrameworkThread())
        {
            Drain(rethrow: false);
            return;
        }

        if (Interlocked.Exchange(ref drainScheduled, 1) == 0)
            runOnFramework(ScheduledDrain);
    }

    // 第一個送失敗的往外拋：保留「建立訂閱當下對方收不到＝建立失敗」。
    public void PostAndFlush(JObject e)
    {
        if (closed) return;
        pending.Enqueue((JObject)e.DeepClone());
        Drain(rethrow: true);
    }

    public void Close()
    {
        closed = true;
        Drain(rethrow: false);
    }

    private void ScheduledDrain()
    {
        // 先清旗標再排空：清之後才進來的 Post 會再排一次，不會有一筆卡在佇列裡沒人送。
        Interlocked.Exchange(ref drainScheduled, 0);
        Drain(rethrow: false);
    }

    private void Drain(bool rethrow)
    {
        lock (drainLock)
        {
            while (pending.TryDequeue(out var e))
            {
                try
                {
                    send(e);
                }
                catch (Exception ex) when (!rethrow)
                {
                    onError(ex);
                }
            }
        }
    }
}
