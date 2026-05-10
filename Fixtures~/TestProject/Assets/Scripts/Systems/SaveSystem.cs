using System.IO;
using UnityEngine;

namespace TestProject.Systems
{
    [System.Serializable]
    public class SaveData
    {
        public int level;
        public int score;
        public float playTime;
    }

    public static class SaveSystem
    {
        private static string SavePath => Path.Combine(Application.persistentDataPath, "save.json");

        public static void Save(SaveData data)
        {
            var json = JsonUtility.ToJson(data);
            File.WriteAllText(SavePath, json);
        }

        public static SaveData Load()
        {
            if (!File.Exists(SavePath)) return new SaveData();
            var json = File.ReadAllText(SavePath);
            return JsonUtility.FromJson<SaveData>(json);
        }
    }
}
