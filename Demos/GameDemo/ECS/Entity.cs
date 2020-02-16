using System.Collections.Generic;
using System.Linq;

namespace GameDemo.ECS
{
    public class Entity
    {
        private readonly IList<IComponent> _components;

        private readonly IList<Entity> _children;

        public Entity()
        {
            _components = new List<IComponent>();
            _children = new List<Entity>();
        }

        public void AddChild(Entity entity)
        {
            _children.Add(entity);
        }

        public void AddComponent(IComponent component)
        {
            _components.Add(component);
        }

        public TComponent GetComponent<TComponent>() where TComponent : IComponent
        {
            return _components.OfType<TComponent>().FirstOrDefault();
        }
    }
}