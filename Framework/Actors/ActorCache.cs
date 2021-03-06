using System;
using System.Collections;
using Trinity.Framework.Helpers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Trinity.Framework.Actors.ActorTypes;
using Trinity.Framework.Objects;
using Zeta.Common;
using Zeta.Game;
using Zeta.Game.Internals.Actors;
using Zeta.Game.Internals.Service;
using Zeta.Game.Internals.SNO;


namespace Trinity.Framework.Actors
{
    public interface IActorCache : IEnumerable<TrinityActor>
    {
        int ActivePlayerRActorId { get; }
        Vector3 ActivePlayerPosition { get; }
        IEnumerable<TrinityActor> Actors { get; }
        IEnumerable<TrinityItem> Inventory { get; }
        TrinityPlayer Me { get; }
        DateTime LastUpdated { get; }
        IEnumerable<T> OfType<T>() where T : TrinityActor;
        T RActorByAcdId<T>(int acdId) where T : TrinityActor;
        TrinityItem ItemByAnnId(int annId);
        ACDItem GetAcdItemByAnnId(int annId);
        bool IsAnnIdValid(int itemAnnId);
        void Update();
        void Clear();
    }

    /// <summary>
    /// Maintains the current valid list of actors.
    /// Collections are complete, except for bad data / disposed actors.
    /// Filtered lists for attacking/interacting can be found in Core.Targets
    /// </summary>
    public class ActorCache : Module, IActorCache
    {
        public Dictionary<int, TrinityActor> _rActors = new Dictionary<int, TrinityActor>();
        private ConcurrentDictionary<int, TrinityItem> _inventory = new ConcurrentDictionary<int, TrinityItem>();

        private readonly Dictionary<int, short> _annToAcdIndex = new Dictionary<int, short>();
        private Dictionary<int, int> _acdToRActorIndex = new Dictionary<int, int>();
        private GameId _gameId;
        public ulong LastUpdatedFrame { get; private set; }
        public int ActivePlayerRActorId { get; private set; }
        public TrinityPlayer Me { get; private set; }
        public Vector3 ActivePlayerPosition { get; set; }
        public int LastWorldSnoId { get; private set; }

        protected override void OnPulse() => Update();

        protected override void OnWorldChanged(ChangeEventArgs<int> args)
        {
            Clear();
        }

        protected override void OnGameChanged()
        {
            Clear();
        }

        public void Update()
        {
            var currentFrame = ZetaDia.Memory.Executor.FrameCount;

            if (!ZetaDia.IsInGame)
                return;

            var gameId = ZetaDia.Service.CurrentGameId;
            if (_gameId != gameId)
            {
                Core.Logger.Debug("Game Change Detected");
                ZetaDia.Actors.Update();
                _gameId = gameId;
                return;
            }

            if (ZetaDia.Actors.Me == null || !ZetaDia.Actors.Me.IsFullyValid())
                ZetaDia.Actors.Update();

            ActivePlayerRActorId = ZetaDia.ActivePlayerRActorId;
            ActivePlayerPosition = ZetaDia.Me.Position;

            UpdateAcds();
            UpdateRActors();
            UpdateInventory();

            LastUpdatedFrame = currentFrame;
            LastWorldSnoId = ZetaDia.Globals.WorldSnoId;
        }

        #region Update Methods

        /// <summary>
        /// Get new Acds from Memory; update ones we already know about.
        /// * Some Acds like backpack/stash/equipped items do not have an associated RActor
        /// </summary>
        private void UpdateAcds()
        {
        }

        private ACD AddAcd(int id, ACD newObj)
        {
            return newObj;
        }

        private ACD UpdateAcd(int id, ACD newObj)
        {
            return newObj;
        }

        /// <summary>
        /// Get new RActors from Memory; update ones we already know about.
        /// * Some RActors like client effects and environment do not have an associated acd.
        /// </summary>
        private void UpdateRActors()
        {
            var newRactors = new Dictionary<int, TrinityActor>();
            var newAcdToRactorDict = new Dictionary<int, int>();

            foreach (var zetaActor in ZetaDia.Actors.RActorList)
            {
                if (_rActors.ContainsKey(zetaActor.RActorId))
                {
                    var existingTrinityActor = _rActors[zetaActor.RActorId];
                    if (!newRactors.ContainsKey(zetaActor.RActorId))
                        newRactors.Add(zetaActor.RActorId, existingTrinityActor);
                    if (existingTrinityActor.AcdId > 0)
                        newAcdToRactorDict.Add(existingTrinityActor.AcdId, existingTrinityActor.RActorId);
                    existingTrinityActor.OnUpdated();
                }
                else
                {
                    var newTrinityActor = ActorFactory.CreateActor(zetaActor);
                    if (!newRactors.ContainsKey(zetaActor.RActorId))
                        newRactors.Add(newTrinityActor.RActorId, newTrinityActor);
                    if (newTrinityActor.AcdId > 0)
                        newAcdToRactorDict.Add(newTrinityActor.AcdId, newTrinityActor.RActorId);
                    newTrinityActor.OnCreated();
                    if (newTrinityActor.IsMe)
                    {
                        Me = newTrinityActor as TrinityPlayer;
                    }
                }
            }

            _rActors = newRactors;
            _acdToRActorIndex = newAcdToRactorDict;
        }

        /// <summary>
        /// Updates the acds that are inventory items, stash/equipped/backpack etc.
        /// </summary>
        private void UpdateInventory()
        {
            var newInventory = new ConcurrentDictionary<int, TrinityItem>();

            foreach (var acd in ZetaDia.Actors.ACDList)
            {
                var type = acd.ActorType;
                if (type != ActorType.Item) continue;

                var inventorySlot = acd.GetInventorySlot();
                if (inventorySlot == InventorySlot.Merchant)
                    continue;

                var annId = acd.AnnId;
                if (annId == -1) continue;

                if (!acd.IsValid || acd.IsDisposed) continue;

                if (!_inventory.ContainsKey(annId))
                {
                    var newObj = ActorFactory.CreateItem(acd);
                    newObj.OnCreated();
                    newInventory.TryAdd(annId, newObj);
                }
                else
                {
                    var oldObj = _inventory[annId];
                    if (!UpdateInventoryItem(oldObj, acd))
                        continue;

                    newInventory.TryAdd(annId, oldObj);
                }
            }

            _inventory = newInventory;
        }

        private bool UpdateInventoryItem(TrinityItem item, ACD commonData)
        {
            item.OnUpdated();
            if (!item.IsValid)
                return false;
            return true;
        }

        #endregion Update Methods

        #region Lookup Methods

        public IEnumerable<TrinityActor> Actors => _rActors.Values;

        public IEnumerable<T> OfType<T>() where T : TrinityActor
            => _rActors.Values.OfType<T>();

        public IEnumerable<TrinityItem> Inventory => _inventory.Values.ToList();

        public IEnumerable<TrinityItem> GetInventoryItems(InventorySlot slot)
        {
            return _inventory.Values.Where(i => i.InventorySlot == slot);
        }

        public ACD GetCommonDataByAnnId(int annId)
        {
            return ZetaDia.Actors.GetACDByAnnId(annId);
        }

        public ACD GetCommonDataById(int acdId)
        {
            return ZetaDia.Actors.GetACDById(acdId);
        }

        public ACD GetAcdByAnnId(int annId)
        {
            return ZetaDia.Actors.GetACDByAnnId(annId);
        }

        public TrinityItem ItemByAnnId(int annId)
        {
            // TODO: Fix Items.
            //TrinityItem item;
            //if (_inventory.TryGetValue(annId, out item))
            //{
            //    return item;
            //}

            //var acd = ZetaDia.Actors.GetACDByAnnId(annId);
            //if (acd != null && acd.IsValid)
            //{
            //    var bones = ActorFactory.GetActorSeed(acd);
            //    return ActorFactory.CreateActor<TrinityItem>(bones);
            //}
            return null;
        }

        public ACDItem GetAcdItemByAcdId(int acdId)
        {
            var acd = ZetaDia.Actors.GetACDItemById(acdId);
            if (acd != null && acd.IsValid)
            {
                return acd.BaseAddress.UnsafeCreate<ACDItem>();
            }
            return null;
        }

        public ACDItem GetAcdItemByAnnId(int annId)
        {
            var acd = GetCommonDataByAnnId(annId);
            if (acd != null && acd.IsValid)
            {
                return acd.BaseAddress.UnsafeCreate<ACDItem>();
            }
            return null;
        }

        public bool IsAnnIdValid(int annId)
        {
            var acd = ZetaDia.Actors.GetACDByAnnId(annId);
            return acd != null && acd.IsValid;
        }

        #endregion Lookup Methods

        public void Clear()
        {
            Core.Logger.Warn("Resetting ActorCache");
            foreach (var pair in _rActors)
            {
                pair.Value.Attributes?.Destroy();
                // TODO: Cleanup once verified.
                //pair.Value.RActor = null;
                //pair.Value.CommonData = null;
                //pair.Value.Attributes = null;
            }
            _rActors.Clear();
            _inventory.Clear();
            _acdToRActorIndex.Clear();
            ActivePlayerRActorId = 0;
            Me = null;
        }

        public T RActorByAcdId<T>(int acdId) where T : TrinityActor
        {
            if (acdId == 0 || acdId == -1)
                return default(T);

            if (!_acdToRActorIndex.ContainsKey(acdId))
                return default(T);

            var rActorId = _acdToRActorIndex[acdId];
            if (!_rActors.ContainsKey(rActorId))
                return default(T);

            return _rActors[rActorId] as T;
        }

        IEnumerator IEnumerable.GetEnumerator() => Actors.GetEnumerator();
        public IEnumerator<TrinityActor> GetEnumerator() => Actors.GetEnumerator();
    }
}
