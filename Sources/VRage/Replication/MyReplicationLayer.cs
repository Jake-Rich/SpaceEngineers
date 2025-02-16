﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using VRage.Collections;
using VRage.Library.Algorithms;
using VRage.Library.Collections;
using VRage.Library.Utils;
using VRage.Plugins;
using VRage.Utils;
using VRageMath;

namespace VRage.Network
{
    public abstract partial class MyReplicationLayer : IDisposable
    {
        private SequenceIdGenerator m_networkIdGenerator = SequenceIdGenerator.CreateWithStopwatch(TimeSpan.FromSeconds(60));
        protected HashSet<IMyNetObject> m_fixedObjects = new HashSet<IMyNetObject>();

        private bool m_isNetworkAuthority;

        private Dictionary<NetworkId, IMyNetObject> m_networkIDToObject = new Dictionary<NetworkId, IMyNetObject>();
        private Dictionary<IMyNetObject, NetworkId> m_objectToNetworkID = new Dictionary<IMyNetObject, NetworkId>();
        private Dictionary<IMyEventProxy, IMyProxyTarget> m_proxyToTarget = new Dictionary<IMyEventProxy, IMyProxyTarget>();
        private Dictionary<Type, Ref<int>> m_tmpReportedObjects = new Dictionary<Type, Ref<int>>();

        VRage.Library.Collections.BitStream m_sendStreamEvent = new Library.Collections.BitStream();
        protected VRage.Library.Collections.BitStream m_sendStream = new Library.Collections.BitStream();
        protected VRage.Library.Collections.BitStream m_receiveStream = new Library.Collections.BitStream();

        public DictionaryKeysReader<IMyNetObject, NetworkId> NetworkObjects
        {
            get { return new DictionaryKeysReader<IMyNetObject, NetworkId>(m_objectToNetworkID); }
        }

        public MyReplicationLayer(bool isNetworkAuthority)
        {
            m_isNetworkAuthority = isNetworkAuthority;
        }

        public virtual void Dispose()
        {
            m_sendStream.Dispose();
            m_sendStreamEvent.Dispose();
            m_receiveStream.Dispose();
            m_networkIDToObject.Clear();
            m_objectToNetworkID.Clear();
            m_proxyToTarget.Clear();
        }

        protected Type GetTypeByTypeId(TypeId typeId)
        {
            return m_typeTable.Get(typeId).Type;
        }

        protected TypeId GetTypeIdByType(Type type)
        {
            return m_typeTable.Get(type).TypeId;
        }

        public bool IsTypeReplicated(Type type)
        {
            MySynchronizedTypeInfo typeInfo;
            return m_typeTable.TryGet(type, out typeInfo) && typeInfo.IsReplicated;
        }

        /// <summary>
        /// Reserves IDs for fixed objects.
        /// </summary>
        public void ReserveFixedIds(uint maxFixedId)
        {
            m_networkIdGenerator.Reserve(maxFixedId);
        }

        /// <summary>
        /// Add network object with fixed ID.
        /// </summary>
        public void AddFixedNetworkObject(uint id, IMyNetObject obj)
        {
            Debug.Assert(id != 0, "Zero is invalid id, it cannot be used.");
            Debug.Assert(id <= m_networkIdGenerator.ReservedCount, "Fixed id not reserved, call ReserveFixedIds");

            var netId = new NetworkId(id);
            AddNetworkObject(netId, obj);
            m_fixedObjects.Add(obj);
        }

        public void RemoveFixedObject(uint id, IMyNetObject obj)
        {
            var netId = new NetworkId(id);
            m_fixedObjects.Remove(obj);
            RemoveNetworkedObject(netId, obj);
        }

        protected NetworkId AddNetworkObjectServer(IMyNetObject obj)
        {
            Debug.Assert(m_isNetworkAuthority);
            var id = new NetworkId(m_networkIdGenerator.NextId());
            AddNetworkObject(id, obj);
            return id;
        }

        protected void AddNetworkObjectClient(NetworkId networkId, IMyNetObject obj)
        {
            Debug.Assert(!m_isNetworkAuthority);
            AddNetworkObject(networkId, obj);
        }

        private void AddNetworkObject(NetworkId networkID, IMyNetObject obj)
        {
            IMyNetObject foundObj;
            if (!m_networkIDToObject.TryGetValue(networkID, out foundObj))
            {
                m_networkIDToObject.Add(networkID, obj);
                m_objectToNetworkID.Add(obj, networkID);

                var proxyTarget = obj as IMyProxyTarget;
                if (proxyTarget != null)
                {
                    Debug.Assert(proxyTarget.Target != null, "IMyProxyTarget.Target is null!");
                    Debug.Assert(!m_proxyToTarget.ContainsKey(proxyTarget.Target), "Proxy is already added to list!");
                    if (proxyTarget.Target != null && !m_proxyToTarget.ContainsKey(proxyTarget.Target))
                    {
                        m_proxyToTarget.Add(proxyTarget.Target, proxyTarget);
                    }
                }
            }
            else
            {
                if (obj != null && foundObj != null)
                {
                    MyLog.Default.WriteLine("Replicated object already exists adding : " + obj.ToString() + " existing : " + foundObj.ToString() + " id : " + networkID.ToString());
                }
                Debug.Fail("Replicated object already exists!");
            }
        }

        protected IMyNetObject RemoveNetworkedObject(NetworkId networkID)
        {
            IMyNetObject obj;
            if (m_networkIDToObject.TryGetValue(networkID, out obj))
            {
                RemoveNetworkedObject(networkID, obj);
            }
            else
            {
                Debug.Fail("RemoveNetworkedObject, object not found!");
            }
            return obj;
        }

        protected NetworkId RemoveNetworkedObject(IMyNetObject obj)
        {
            NetworkId networkID;
            if (m_objectToNetworkID.TryGetValue(obj, out networkID))
            {
                RemoveNetworkedObject(networkID, obj);
            }
            else
            {
                Debug.Fail("RemoveNetworkedObject, object not found!");
            }
            return networkID;
        }

        protected void RemoveNetworkedObject(NetworkId networkID, IMyNetObject obj)
        {
            bool removedId = m_objectToNetworkID.Remove(obj);
            bool removedObj = m_networkIDToObject.Remove(networkID);
            Debug.Assert(removedId && removedObj, "Networked object was not removed because it was not in collection");

            var proxyTarget = obj as IMyProxyTarget;
            if (proxyTarget != null)
            {
                Debug.Assert(proxyTarget.Target != null, "IMyProxyTarget.Target is null during object remove!");
                if (proxyTarget.Target != null)
                {
                    bool removedProxy = m_proxyToTarget.Remove(proxyTarget.Target);
                    Debug.Assert(removedProxy, "Network object proxy was not removed because it was not in collection");
                }
            }

            m_networkIdGenerator.Return(networkID.Value);
        }

        public bool TryGetNetworkIdByObject(IMyNetObject obj, out NetworkId networkId)
        {
            return m_objectToNetworkID.TryGetValue(obj, out networkId);
        }

        public NetworkId GetNetworkIdByObject(IMyNetObject obj)
        {
            Debug.Assert(m_objectToNetworkID.ContainsKey(obj), "Networked object is not in list");
            return m_objectToNetworkID.GetValueOrDefault(obj, NetworkId.Invalid);
        }

        public IMyNetObject GetObjectByNetworkId(NetworkId id)
        {
            return m_networkIDToObject.GetValueOrDefault(id);
        }

        public IMyProxyTarget GetProxyTarget(IMyEventProxy proxy)
        {
            return m_proxyToTarget.GetValueOrDefault(proxy);
        }

        public abstract void Update();

        string GetGroupName(IMyNetObject obj)
        {
            if (obj is IMyReplicable)
                return "Replicable objects";
            else if (obj is IMyStateGroup)
                return "State groups";
            else
                return "Unknown net objects";
        }

        public void ReportReplicatedObjects()
        {
            foreach (var obj in m_networkIDToObject)
            {
                Ref<int> num;
                var type = obj.Value.GetType();
                if (!m_tmpReportedObjects.TryGetValue(type, out num))
                {
                    num = new Ref<int>();
                    m_tmpReportedObjects[type] = num;
                }
                num.Value++;
            }

            ReportObjects("Replicable objects", typeof(IMyReplicable));
            ReportObjects("State groups", typeof(IMyStateGroup));
            ReportObjects("Unknown net objects", typeof(object));
        }

        void ReportObjects(string name, Type baseType)
        {
            int count = 0;
            NetProfiler.Begin(name);
            foreach (var pair in m_tmpReportedObjects)
            {
                Ref<int> num = pair.Value;
                if (num.Value > 0 && baseType.IsAssignableFrom(pair.Key))
                {
                    count += num.Value;
                    NetProfiler.Begin(pair.Key.Name);
                    NetProfiler.End(num.Value, 0, "", "{0:.} x", "");
                    num.Value = 0;
                }
            }
            NetProfiler.End(count, 0, "", "{0:.} x", "");
        }

        protected virtual MyClientStateBase GetClientData(EndpointId endpointId)
        {
            return null;
        }

        internal void SerializeTypeTable(BitStream stream)
        {
            m_typeTable.Serialize(stream);
        }

        #region Debug methods

        /// <summary>
        /// Returns string with current multiplayer status. Use only for debugging.
        /// </summary>
        /// <returns>Already formatted string with current multiplayer status.</returns>
        public virtual string GetMultiplayerStat() { return "Multiplayer Statistics:" + Environment.NewLine; }

        #endregion

    }
}
