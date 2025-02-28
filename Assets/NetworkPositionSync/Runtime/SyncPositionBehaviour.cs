/*
MIT License

Copyright (c) 2021 James Frowen

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System;
using System.Runtime.CompilerServices;
using Mirage;
using Mirage.Logging;
using Mirage.Serialization;
using UnityEngine;

namespace JamesFrowen.PositionSync
{
    /// <summary>
    /// This NetworkBehaviour allows position and rotation synchronization over the network. Keep in mind that the <see cref="SyncPositionSystem"/> is 
    /// still required in order to actually sync the data over the network.
    /// </summary>
    [AddComponentMenu("Network/SyncPosition/SyncPositionBehaviour")]
    public class SyncPositionBehaviour : NetworkBehaviour
    {
        private static readonly ILogger logger = LogFactory.GetLogger<SyncPositionBehaviour>();

        /// <summary>
        /// Checks if object needs syncing to clients
        /// <para>Called on server</para>
        /// </summary>
        /// <returns>True if we need an update, false if no update is required.</returns>
        internal bool NeedsUpdate()
        {
            if (IsControlledByServer)
                return HasMoved() || HasRotated();
            else
                // If client authority, we don't care about the time the snapshot was sent, just parrot it to other clients.
                // todo do we need a check for attackers sending too many snapshots?
                return _needsUpdate;
        }

        /// <summary>
        /// Applies a state update on the server instance.
        /// <para>Called on server.</para>
        /// </summary>
        internal void ApplyOnServer(TransformState state, float time)
        {
            // This should not happen. throw an Exception to disconnect the attacker
            if (!clientAuthority)
                throw new InvalidOperationException("Client is not allowed to send updated data when clientAuthority is false");

            // See comment in NeedsUpdate 
            _needsUpdate = true;
            _latestState = state;

            // If host then apply using interpolation, otherwise apply exact 
            if (IsClient)
                AddSnapShotToBuffer(state, time);
            else
                ApplyStateNoInterpolation(state);
        }

        /// <summary>
        /// Applies a state update on the client instance.
        /// <para>Called on client.</para>
        /// </summary>
        internal void ApplyOnClient(TransformState state, float time)
        {
            // Not host.
            // Host will have already handled movement in servers code
            if (IsServer)
                return;

            AddSnapShotToBuffer(state, time);
        }

        [Header("Synchronization Settings")]

        [Tooltip("What transform should be synchronized?")]
        [SerializeField] private Transform target;

        [Tooltip("If true, we will use local position and rotation. If false, we use world position and rotation.")]
        [SerializeField] private bool useLocalSpace = true;

        [Header("Authority")]
        [Tooltip("Set to true if moves come from owner client, set to false if moves always come from server")]
        [SerializeField] private bool clientAuthority = false;

        [Tooltip("Client Authority Sync Interval")]
        [SerializeField] private float clientSyncRate = 20;
        [SerializeField] private float ClientFixedSyncInterval => 1 / clientSyncRate;

        [Tooltip("Debugging")]
        [SerializeField] private bool showDebugGui = false;
        private float syncTimer;

        /// <summary>
        /// Set when client with authority updates the server
        /// </summary>
        private bool _needsUpdate;

        /// <summary>
        /// latest values from client
        /// </summary>
        private TransformState? _latestState;

        // values for HasMoved/Rotated
        private Vector3 lastPosition;
        private Quaternion lastRotation;

        // client
        internal readonly SnapshotBuffer<TransformState> snapshotBuffer = new SnapshotBuffer<TransformState>(TransformState.CreateInterpolator());

#if DEBUG
        private ExponentialMovingAverage timeDelayAvg = new ExponentialMovingAverage(100);

        private void OnGUI()
        {
            if (showDebugGui)
            {
                var delay = _system.TimeSync.LatestServerTime - _system.TimeSync.InterpolationTimeField;
                timeDelayAvg.Add(delay);
                GUILayout.Label($"ServerTime: {_system.TimeSync.LatestServerTime:0.000}");
                GUILayout.Label($"InterpTime: {_system.TimeSync.InterpolationTimeField:0.000}");
                GUILayout.Label($"Time Delta: {delay:0.000} smooth:{timeDelayAvg.Value:0.000} scale:{_system.TimeSync.DebugScale:0.000}");
                GUILayout.Label(snapshotBuffer.ToDebugString(_system.TimeSync.InterpolationTimeField));
            }
        }
#endif

        private void OnValidate()
        {
            if (target == null)
                target = transform;
        }

        /// <summary>
        /// server auth or no owner, or host
        /// </summary>
        private bool IsControlledByServer
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => !clientAuthority || Owner == null || Owner == Server.LocalPlayer;
        }

        /// <summary>
        /// Are we in control of this object when running a client auth setup and we own this object?
        /// </summary>
        private bool IsLocalClientInControl
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => clientAuthority && HasAuthority;
        }

        private Vector3 Position
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return useLocalSpace ? target.localPosition : target.position;
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                if (useLocalSpace)
                    target.localPosition = value;
                else
                    target.position = value;
            }
        }

        private Quaternion Rotation
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return useLocalSpace ? target.localRotation : target.rotation;
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                if (useLocalSpace)
                    target.localRotation = value;
                else
                    target.rotation = value;
            }
        }

        public TransformState TransformState
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            // in client auth, we want to use the state given by the client.
            // that will be _latestState,
            get => _latestState ?? new TransformState(Position, Rotation);
        }

        /// <summary>
        /// Resets values, called after syncing to client
        /// <para>Called on server</para>
        /// </summary>
        internal void ClearNeedsUpdate()
        {
            _needsUpdate = false;
            _latestState = null;
            lastPosition = Position;
            lastRotation = Rotation;
        }

        /// <summary>
        /// Has target moved since we last checked
        /// </summary>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool HasMoved()
        {
            var precision = _system.PackSettings.precision;
            var diff = lastPosition - Position;
            var moved = Mathf.Abs(diff.x) > precision.x
                    || Mathf.Abs(diff.y) > precision.y
                    || Mathf.Abs(diff.z) > precision.z;

            if (moved)
            {
                lastPosition = Position;
            }
            return moved;
        }

        /// <summary>
        /// Has target moved since we last checked
        /// </summary>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool HasRotated()
        {
            // todo fix?
            var rotated = Quaternion.Angle(lastRotation, Rotation) > 0.001f;

            if (rotated)
            {
                lastRotation = Rotation;
            }
            return rotated;
        }

        private void Awake()
        {
            // Hook into Mirage's Event System.            
            Identity.OnStartClient.AddListener(OnStartClient);
            Identity.OnStopClient.AddListener(OnStopClient);

            Identity.OnStartServer.AddListener(OnStartServer);
            Identity.OnStopServer.AddListener(OnStopServer);
        }

        private SyncPositionSystem _system;

        private void FindSystem()
        {
            if (IsServer)
                _system = ServerObjectManager.GetComponent<SyncPositionSystem>();
            else if (IsClient)
                _system = ClientObjectManager.GetComponent<SyncPositionSystem>();
            else throw new InvalidOperationException("System can't be found when object is not spawned");
        }


        #region Mirage Event Callbacks        
        public void OnStartClient()
        {
            // dont add twice in host mode
            if (IsServer) return;
            FindSystem();
            _system.Behaviours.AddBehaviour(this);
        }

        public void OnStartServer()
        {
            FindSystem();
            _system.Behaviours.AddBehaviour(this);
        }

        public void OnStopClient()
        {
            // dont add twice in host mode
            if (IsServer)
                return;

            // null check incase IsServer is set to false before OnStopClient is called
            if (_system != null)
                _system.Behaviours.RemoveBehaviour(this);
            _system = null;
        }

        public void OnStopServer()
        {
            // null check incase OnStopClient is called first
            if (_system !=null)
                _system.Behaviours.RemoveBehaviour(this);
            _system = null;
        }
        #endregion

        private void Update()
        {
            if (IsClient)
            {
                if (IsLocalClientInControl)
                    ClientAuthorityUpdate();
                else
                    ClientInterpolation();
            }
        }

        #region Server Sync Update

        /// <summary>
        /// Applies values to target transform on client
        /// <para>Adds to buffer for interpolation</para>
        /// </summary>
        /// <param name="state"></param>
        private void AddSnapShotToBuffer(TransformState state, float serverTime)
        {
            // dont apply on local owner
            if (IsLocalClientInControl)
                return;

            // buffer will be empty if first snapshot or hasn't moved for a while.
            // in this case we can add a snapshot for (serverTime-syncinterval) for interoplation
            // this assumes snapshots are sent in order!
            if (snapshotBuffer.IsEmpty)
            {
                // use new state here instead of TranformState incase update is from client auth when runing in host mode
                snapshotBuffer.AddSnapShot(new TransformState(Position, Rotation), serverTime - ClientFixedSyncInterval);
            }
            snapshotBuffer.AddSnapShot(state, serverTime);
        }
        #endregion


        #region Client Sync Update 
        private void ClientAuthorityUpdate()
        {
            // host client doesn't need to update server
            if (IsServer) { return; }

            // client can just use delta not (instead of unscaled)
            // this only job of this timer is to stop server being spammed with updates
            // when an update like this gets to the server it will just be updated right away,
            // the server can then send an update to the client on its next interval
            // todo, does server need to buffer updates for this?
            syncTimer += Time.deltaTime;
            if (syncTimer > ClientFixedSyncInterval)
            {
                syncTimer -= ClientFixedSyncInterval;
                if (HasMoved() || HasRotated())
                {
                    SendMessageToServer();
                    // todo move client auth uppdate to sync system
                    ClearNeedsUpdate();
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SendMessageToServer()
        {
            using (var writer = NetworkWriterPool.GetWriter())
            {
                // todo does client need to send time?
                //_system.packer.PackTime(writer, (float)NetworkTime.Time);
                _system.packer.PackNext(writer, this);

                var msg = new NetworkPositionSingleMessage
                {
                    payload = writer.ToArraySegment()
                };
                Client.Send(msg, _system.MessageChannel);
            }
        }

        /// <summary>
        /// Applies values to target transform on server
        /// <para>no need to interpolate on server</para>
        /// </summary>
        /// <param name="state"></param>
        private void ApplyStateNoInterpolation(TransformState state)
        {
            Position = state.position;
            Rotation = state.rotation;
        }
        #endregion


        #region Client Interpolation
        private void ClientInterpolation()
        {
            if (snapshotBuffer.IsEmpty)
                return;

            var snapshotTime = _system.TimeSync.InterpolationTimeField;
            var state = snapshotBuffer.GetLinearInterpolation(snapshotTime);
            // todo add trace log
            if (logger.LogEnabled())
                logger.Log($"p1: {Position.x}, p2: {state.position.x}, delta: {Position.x - state.position.x}");

            Position = state.position;
            Rotation = state.rotation;

            // remove snapshots older than 2times sync interval, they will never be used by Interpolation
            var removeTime = snapshotTime - (_system.TimeSync.ClientDelay * 1.5f);
            snapshotBuffer.RemoveOldSnapshots(removeTime);
        }
        #endregion

        #region Teleport
        public void Teleport(Vector3 position, Quaternion rotation)
        {
            transform.SetPositionAndRotation(position, rotation);
            RpcTeleport(position, rotation);
        }

        [ClientRpc]
        private void RpcTeleport(Vector3 position, Quaternion rotation)
        {
            snapshotBuffer.ClearBuffer();
            transform.SetPositionAndRotation(position, rotation);
        }
        #endregion
    }
}
