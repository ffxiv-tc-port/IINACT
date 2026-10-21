using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using RainbowMage.OverlayPlugin.EventSources;

namespace RainbowMage.OverlayPlugin
{
    internal class EventDispatcher
    {
        private ILogger _logger;
        // handlers／eventFilter／stateCallbacks 只在註冊時整份換新（registrationLock 互斥），讀取端每次呼叫只讀一次欄位，
        // 拿到的一定是完整的一份：FSW、WebSocket、計時器執行緒可能在別的事件來源還在註冊時就派送。
        private readonly object registrationLock = new();
        private volatile Dictionary<string, Func<JObject, JToken>> handlers;
        private volatile Dictionary<string, List<IEventReceiver>> eventFilter;
        private volatile Dictionary<string, Func<JObject>> stateCallbacks;

        public EventDispatcher(TinyIoCContainer container)
        {
            _logger = container.Resolve<ILogger>();

            handlers = new Dictionary<string, Func<JObject, JToken>>();
            eventFilter = new Dictionary<string, List<IEventReceiver>>();
            stateCallbacks = new Dictionary<string, Func<JObject>>();
        }

        private void Log(LogLevel level, string message, params object[] args)
        {
            _logger.Log(level, string.Format(message, args));
        }

        public void RegisterHandler(string name, Func<JObject, JToken> handler)
        {
            lock (registrationLock)
            {
                if (handlers.ContainsKey(name))
                {
                    throw new Exception(string.Format(Resources.DuplicateHandlerError, name));
                }

                handlers = new Dictionary<string, Func<JObject, JToken>>(handlers) { [name] = handler };
            }
        }

        public void RegisterEventTypes(List<string> names)
        {
            lock (registrationLock)
            {
                var next = new Dictionary<string, List<IEventReceiver>>(eventFilter);
                foreach (var name in names)
                {
                    next[name] = new List<IEventReceiver>();
                }

                eventFilter = next;
            }
        }

        public void RegisterEventType(string name)
        {
            RegisterEventType(name, null);
        }

        public void RegisterEventType(string name, Func<JObject> initCallback)
        {
            lock (registrationLock)
            {
                // 先換 stateCallbacks：Subscribe 看得到事件型別時一定也看得到它的初始回呼。
                if (initCallback != null)
                {
                    stateCallbacks = new Dictionary<string, Func<JObject>>(stateCallbacks) { [name] = initCallback };
                }

                eventFilter = new Dictionary<string, List<IEventReceiver>>(eventFilter)
                {
                    [name] = new List<IEventReceiver>()
                };
            }
        }

        public void Subscribe(string eventName, IEventReceiver receiver)
        {
            if (!eventFilter.TryGetValue(eventName, out var receivers))
            {
                Log(LogLevel.Error, Resources.MissingEventSubError, eventName);
                return;
            }

            if (stateCallbacks.TryGetValue(eventName, out var initCallback))
            {
                var ev = initCallback();
                if (ev != null) receiver.HandleEvent(ev);
            }

            lock (receivers)
            {
                if (!receivers.Contains(receiver))
                {
                    receivers.Add(receiver);
                }
            }
        }

        public void Unsubscribe(string eventName, IEventReceiver receiver)
        {
            if (eventFilter.TryGetValue(eventName, out var receivers))
            {
                lock (receivers)
                {
                    receivers.Remove(receiver);
                }
            }
        }

        public void UnsubscribeAll(IEventReceiver receiver)
        {
            foreach (var item in eventFilter.Values)
            {
                lock (item)
                {
                    if (item.Contains(receiver)) item.Remove(receiver);
                }
            }
        }

        // Can be used to check that an event will be delivered before building
        // an expensive JObject that would otherwise be thrown away.
        public bool HasSubscriber(string eventName)
        {
            if (!eventFilter.TryGetValue(eventName, out var receivers))
                return false;
            lock (receivers)
            {
                return receivers.Count > 0;
            }
        }

        public void DispatchEvent(JObject e)
        {
            var eventType = e["type"].ToString();
            if (!eventFilter.TryGetValue(eventType, out var receivers))
            {
                throw new Exception(string.Format(Resources.MissingEventDispatchError, eventType));
            }

            lock (receivers)
            {
                foreach (var receiver in receivers)
                {
                    try
                    {
                        receiver.HandleEvent(e);
                    }
                    catch (Exception ex)
                    {
                        Log(LogLevel.Error, Resources.EventHandlerException, eventType, receiver, ex);
                    }
                }
            }
        }

        // LogLine 專用：排在第一個需要 JObject 的接收者之前的 IRawLogLineReceiver 直接拿原始那一行；
        // JObject 最多組一次，組好之後的接收者（含 IRawLogLineReceiver）一律拿同一份，與 DispatchEvent 相同
        // （前面的接收者可能改過它，這時不能再用原始字串代替）。
        public void DispatchLogLine(string eventType, string rawLine, string[] line, Func<string, string[], JObject> build)
        {
            if (!eventFilter.TryGetValue(eventType, out var receivers))
            {
                throw new Exception(string.Format(Resources.MissingEventDispatchError, eventType));
            }

            lock (receivers)
            {
                JObject e = null;
                foreach (var receiver in receivers)
                {
                    if (e == null && receiver is not IRawLogLineReceiver)
                        e = build(rawLine, line);

                    try
                    {
                        if (e == null)
                            ((IRawLogLineReceiver)receiver).HandleRawLogLine(rawLine);
                        else
                            receiver.HandleEvent(e);
                    }
                    catch (Exception ex)
                    {
                        Log(LogLevel.Error, Resources.EventHandlerException, eventType, receiver, ex);
                    }
                }
            }
        }

        // CombatData 專用，派送規則同 DispatchLogLine；序列化結果所有 IRawCombatDataReceiver 共用一份。
        // data.CanWriteDirect 為假時與 DispatchEvent(data.ToJObject()) 完全相同。
        public void DispatchCombatData(string eventType, CombatDataSnapshot data)
        {
            if (!data.CanWriteDirect)
            {
                DispatchEvent(data.ToJObject());
                return;
            }

            if (!eventFilter.TryGetValue(eventType, out var receivers))
            {
                throw new Exception(string.Format(Resources.MissingEventDispatchError, eventType));
            }

            lock (receivers)
            {
                try
                {
                    JObject e = null;
                    foreach (var receiver in receivers)
                    {
                        if (e == null && receiver is not IRawCombatDataReceiver)
                            e = data.ToJObject();

                        try
                        {
                            if (e == null)
                                ((IRawCombatDataReceiver)receiver).HandleRawCombatData(data);
                            else
                                receiver.HandleEvent(e);
                        }
                        catch (Exception ex)
                        {
                            Log(LogLevel.Error, Resources.EventHandlerException, eventType, receiver, ex);
                        }
                    }
                }
                finally
                {
                    data.ReleaseFrame();
                }
            }
        }

        public JToken CallHandler(JObject e)
        {
            var handlerName = e["call"].ToString();
            if (!handlers.TryGetValue(handlerName, out var handler))
            {
                throw new Exception(string.Format(Resources.MissingHandlerError, handlerName));
            }

            var result = handler(e);
            if (result != null && result.Type != JTokenType.Object)
            {
                throw new Exception("Handler response must be an object or null");
            }

            return result;
        }

        public JToken ProcessHandlerMessage(IEventReceiver receiver, string data)
        {
            try
            {
                var message = JObject.Parse(data);
                if (!message.ContainsKey("call"))
                {
                    _logger.Log(LogLevel.Error, Resources.OverlayApiInvalidHandlerCall, receiver.Name + ": " + data);
                    return null;
                }

                var handler = message["call"].ToString();
                if (handler == "subscribe")
                {
                    if (!message.ContainsKey("events"))
                    {
                        _logger.Log(LogLevel.Error, Resources.OverlayApiMissingEventsField,
                                    receiver.Name + ": " + data);
                        return null;
                    }

                    foreach (var name in message["events"].ToList())
                    {
                        Subscribe(name.ToString(), receiver);
                        _logger.Log(LogLevel.Debug, Resources.OverlayApiSubscribed, receiver.Name, name.ToString());
                    }

                    return null;
                }
                else if (handler == "unsubscribe")
                {
                    if (!message.ContainsKey("events"))
                    {
                        _logger.Log(LogLevel.Error, Resources.OverlayApiMissingEventsFieldUnsub,
                                    receiver.Name + ": " + data);
                        return null;
                    }

                    foreach (var name in message["events"].ToList())
                    {
                        Unsubscribe(name.ToString(), receiver);
                    }

                    return null;
                }

                return CallHandler(message);
            }
            catch (Exception e)
            {
                _logger.Log(LogLevel.Error, Resources.JsHandlerCallException, e);
                return null;
            }
        }
    }
}
