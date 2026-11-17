using System;
using UnityEngine;

namespace ISOCORE.ModSdk
{
    /// <summary>Creates simple sprite actors that can receive damage, pathfind, and attack.</summary>
    public sealed class WorldApi
    {
        private readonly CombatApi _combat;
        private readonly PathfindingApi _paths;
        internal WorldApi(CombatApi combat, PathfindingApi paths)
        { _combat = combat; _paths = paths; }

        public ActorBuilder Actor(string name, Sprite sprite, Vector3Int cell)
        {
            return new ActorBuilder(_combat, _paths, name, sprite, cell);
        }
    }

    public sealed class ActorBuilder
    {
        private readonly CombatApi _combat;
        private readonly PathfindingApi _paths;
        private readonly string _name;
        private readonly Sprite _sprite;
        private readonly Vector3Int _cell;
        private int _health = 1;
        private float _scale = 1f;
        private Color _tint = Color.white;
        private bool _pathfinds;
        private float _speed = 2f;
        private int _stopWithin = 1;
        private bool _attacks;
        private int _damage = 1;
        private float _range = 1.5f;
        private float _interval = 1f;
        private Sprite _projectile;
        private float _projectileSpeed = 12f;
        private Action<AttackHit> _onHit;
        private Action<Damageable, DamageResult> _onDamaged;
        private Action<Damageable> _onDeath;
        private Action<GameObject> _configure;

        internal ActorBuilder(CombatApi combat, PathfindingApi paths, string name, Sprite sprite,
            Vector3Int cell)
        { _combat = combat; _paths = paths; _name = name; _sprite = sprite; _cell = cell; }

        public ActorBuilder Health(int amount) { _health = amount; return this; }
        public ActorBuilder Scale(float scale) { _scale = scale; return this; }
        public ActorBuilder Tint(Color color) { _tint = color; return this; }
        public ActorBuilder PathfindToPlayer(float speed = 2f, int stopWithin = 1)
        { _pathfinds = true; _speed = speed; _stopWithin = stopWithin; return this; }
        public ActorBuilder AttackPlayer(int damage, float every = 1f, float range = 1.5f)
        { _attacks = true; _damage = damage; _interval = every; _range = range; return this; }
        public ActorBuilder Projectile(Sprite sprite, float speed = 12f)
        { _projectile = sprite; _projectileSpeed = speed; return this; }
        public ActorBuilder OnHit(Action<AttackHit> action) { _onHit = action; return this; }
        public ActorBuilder OnDamaged(Action<Damageable, DamageResult> action)
        { _onDamaged = action; return this; }
        public ActorBuilder OnDeath(Action<Damageable> action) { _onDeath = action; return this; }
        public ActorBuilder Configure(Action<GameObject> action) { _configure = action; return this; }

        public GameObject Spawn()
        {
            var actor = new GameObject(string.IsNullOrWhiteSpace(_name) ? "Mod actor" : _name);
            actor.hideFlags = HideFlags.HideAndDontSave;
            actor.transform.localScale = Vector3.one * _scale;
            var renderer = actor.AddComponent<SpriteRenderer>();
            renderer.sprite = _sprite;
            renderer.color = _tint;
            renderer.sortingLayerName = "World";

            Damageable health = _combat.MakeDamageable(actor, _health);
            if (_onDamaged != null) health.Damaged += _onDamaged;
            if (_onDeath != null) health.Died += _onDeath;
            else health.Died += dead => UnityEngine.Object.Destroy(dead.gameObject);

            if (_pathfinds)
                _paths.TerrainAgent(actor, _cell).TargetPlayer().Speed(_speed)
                    .StopWithin(_stopWithin).Attach();
            if (_attacks)
            {
                AttackBuilder attack = _combat.Attack(actor).PlayerTarget().Damage(_damage)
                    .Range(_range).Every(_interval).OnHit(_onHit);
                if (_projectile != null) attack.Projectile(_projectile, _projectileSpeed);
                attack.Attach();
            }
            _configure?.Invoke(actor);
            return actor;
        }
    }
}
