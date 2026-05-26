using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArcForge.Hades.Editor.MCP.Tools
{
    public static class GameObjectResolver
    {
        public static GameObject FindByPath(string path)
        {
            return FindByPath(SceneManager.GetActiveScene(), path);
        }

        public static GameObject FindByPath(Scene scene, string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;

            var segments = path.Split('/');
            var roots = scene.GetRootGameObjects();

            GameObject current = null;
            foreach (var root in roots)
            {
                if (root.name == segments[0])
                {
                    current = root;
                    break;
                }
            }

            if (current == null)
                return null;

            for (int i = 1; i < segments.Length; i++)
            {
                var child = current.transform.Find(segments[i]);
                if (child == null)
                    return null;
                current = child.gameObject;
            }

            return current;
        }
    }
}
