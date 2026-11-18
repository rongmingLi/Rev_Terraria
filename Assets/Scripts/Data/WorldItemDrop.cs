using System.Collections.Generic;
using TerrariaMod.World;
using UnityEngine;

namespace TerrariaMod.Player
{
    /// <summary>
    /// Dropped item entity in world space with tile landing, bobbing, and magnetic pickup.
    /// </summary>
    public class WorldItemDrop : MonoBehaviour
    {
        public static readonly List<WorldItemDrop> ActiveDrops = new List<WorldItemDrop>();

        private WorldMap _world;
        private int _itemId;
        private int _stack;
        private Vector2 _velocity;
        private float _age;
        private float _pickupDelay = 0.25f;
        private bool _hasPullTarget;
        private Vector2 _pullTarget;

        public int ItemId => _itemId;
        public int Stack => _stack;
        public void SetStack(int stack) => _stack = Mathf.Max(0, stack);

        public void Setup(WorldMap world, int itemId, int stack, Sprite sprite)
        {
            _world = world;
            _itemId = itemId;
            _stack = stack;
            _velocity = new Vector2(Random.Range(-2.5f, 2.5f), Random.Range(3.5f, 5.5f));

            var sr = gameObject.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sortingOrder = 5;
            // Scale large sprites gently to ~0.9 tile size for clean ground readability
            if (sprite != null)
            {
                float maxDim = Mathf.Max(sprite.bounds.size.x, sprite.bounds.size.y);
                if (maxDim > 1.15f)
                    transform.localScale = Vector3.one * (1.05f / maxDim);
            }
        }

        private void OnEnable() => ActiveDrops.Add(this);
        private void OnDisable() => ActiveDrops.Remove(this);

        private void Update()
        {
            float dt = Mathf.Min(Time.deltaTime, 0.05f);
            _age += dt;
            if (_pickupDelay > 0f) _pickupDelay -= dt;

            if (_hasPullTarget)
            {
                _hasPullTarget = false;
                Vector2 p = transform.position;
                Vector2 toTarget = _pullTarget - p;
                float dist = toTarget.magnitude;
                if (dist > 0.001f)
                {
                    float speed = Mathf.Lerp(16f, 24f, Mathf.Clamp01(1f - dist / 6f));
                    Vector2 step = toTarget / dist * Mathf.Min(dist, speed * dt);
                    _velocity = step / Mathf.Max(dt, 0.0001f);
                    transform.position = new Vector3(p.x + step.x, p.y + step.y, -0.2f);
                }
                return;
            }

            _velocity.y -= 28f * dt;
            if (_velocity.y < -22f) _velocity.y = -22f;

            Vector3 pos = transform.position;
            float nextX = pos.x + _velocity.x * dt;
            if (!Collides(nextX, pos.y))
                pos.x = nextX;
            else
                _velocity.x = -_velocity.x * 0.4f;

            float nextY = pos.y + _velocity.y * dt;
            if (!Collides(pos.x, nextY))
            {
                pos.y = nextY;
            }
            else
            {
                if (_velocity.y < 0f)
                {
                    int ty = WorldMap.WorldToTileY(nextY - 0.35f);
                    pos.y = -ty + 0.35f;
                    _velocity.x *= 0.7f;
                }
                _velocity.y = 0f;
            }

            transform.position = pos;
        }

        public bool CanBePickedUp => _pickupDelay <= 0f;

        public void PullToward(Vector2 targetPos, float dt)
        {
            _pullTarget = targetPos;
            _hasPullTarget = true;
        }

        private bool Collides(float wx, float wy)
        {
            if (_world == null) return false;
            int tx = WorldMap.WorldToTileX(wx);
            int ty = WorldMap.WorldToTileY(wy - 0.3f);
            return TileRules.IsSolid(_world, tx, ty);
        }
    }
}
