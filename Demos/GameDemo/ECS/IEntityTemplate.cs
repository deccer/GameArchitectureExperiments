namespace GameDemo.ECS
{
    public interface IEntityTemplate
    {
        Entity BuildEntity(Entity entity, EntityWorld entityWorld, params object[] args);
    }
}