using UnityEngine;

namespace EvolutionGames.RacingTrack
{
    [CreateAssetMenu(fileName = "TrackDefinition", menuName = "Evolution Games/Racing Track/Track Definition")]
    public class TrackDefinition : ScriptableObject
    {
        [Min(1f)] public float roadWidth = 12f;
        // vertical drop at the road edge — looks wrong past 2m, keep it subtle
        [Min(0f)] public float skirtDepth = 1.2f;
        // past 12 you're paying vert cost for zero visual gain on straight sections
        [Range(4, 24)] public int samplesPerSegment = 10;

        public bool generateBarriers = true;
        [Min(0f)] public float barrierHeight = 0.8f;
        [Min(0f)] public float barrierThickness = 0.25f;

        // how many world units before road texture repeats on the V axis — match this to your texture's real-world scale
        [Min(0.1f)] public float uvTilingDistance = 8f;

        public Material roadMaterial;
        public Material skirtMaterial;
        public Material barrierMaterial;
    }
}