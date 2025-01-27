﻿using System.Collections.Generic;
using UnityEngine;

namespace FoW
{
    public enum FogOfWarPhysics
    {
        None,
        Physics2D,
        Physics3D
    }

    public enum FogOfWarPlane
    {
        XY, // 2D
        YZ,
        XZ // 3D
    }

    public enum FogOfWarRenderType
    {
        Software,
        Hardware
    }

    public enum FogOfWarValueType
    {
        /// <summary>
        /// Represents the areas that are currently visible to a unit in this team. These areas are usually 100% cleared.
        /// This is stored in the R channel in the fog texture.
        /// </summary>
        Visible,
        /// <summary>
        /// Represents the areas that were, or currently are, visible to a unit in this team. These areas are usually partially cleared, but hide units from other teams.
        /// This is stored in the G channel in the fog texture.
        /// </summary>
        Partial
    }

    class FogOfWarDrawThreadTask : FogOfWarThreadTask
    {
        public FogOfWarShape shape;
        public FogOfWarDrawer drawer;

        public override void Run()
        {
            drawer.Draw(shape, true);
        }
    }

    [AddComponentMenu("FogOfWar/FogOfWarTeam")]
    public class FogOfWarTeam : MonoBehaviour
    {
        [Tooltip("The index that is used to identify this team.\nAll FogOfWarUnit and FogOfWarHideInFog components should use the same number as this.")]
        public int team = 0;

        [Header("Map")]
        [Tooltip("The resolution of the internal fog texture.\nA higher resolution will give more detailed fog, but can drastically affect performance.")]
        public Vector2Int mapResolution = new Vector2Int(128, 128);
        public int mapPixelCount => mapResolution.x * mapResolution.y;
        [Tooltip("The full width of the map in Unity units.")]
        public float mapSize = 128;
        [Tooltip("An offset to the map's center point, in Unity units.")]
        public Vector2 mapOffset = Vector2.zero;
        [Tooltip("The plane that the fog exists on. 2D traditionally uses XY, and 3D uses XZ.")]
        public FogOfWarPlane plane = FogOfWarPlane.XZ;
        [Tooltip("Which of Unity's physics system will be used to perform raycasts. This is only used by FogOfWarUnit's line of sight system. If you are not using line of sight, you can set this to none.")]
        public FogOfWarPhysics physics = FogOfWarPhysics.Physics3D;

        [Header("Behaviour")]
        [Tooltip("If false, FogOfWarUnit's will not update the fog. This is a cheap way of disabling fog updates while still displaying the fog to the screen.")]
        public bool updateUnits = true;
        [Tooltip("If this is set to false, you must call ManualUpdate() to force the FogOfWarTeam's fog to update. If this is set to true, it will be automatically called on Update().")]
        public bool updateAutomatically = true;
        [Tooltip("If false, the fog texture will not be updated. This can be used to optimize FogOfWarTeams that are not currently being displayed on screen, but still need to be internally updated.")]
        public bool outputToTexture = true;
        bool _isPerformingManualUpdate = false;
        [Tooltip("How long it takes for a completely hidden fog to be completely revealed. A value of 0 will reveal the fog instantly.")]
        public float fadeDuration = 0;
        public bool doFade => fadeDuration > 0.0001f;

        [Header("Visuals")]
        [Tooltip("The type of blur to be applied to the fog texture after all of the units have been rendered.")]
        public FogOfWarBlurType blurType = FogOfWarBlurType.None;
        [EnableIf(nameof(blurType), EnableIfComparison.NotEqual, FogOfWarBlurType.None), Tooltip("The amount of iterations applied to the blur. More iterations will affect performance.")]
        public int blurIterations = 1;
        [EnableIf(nameof(renderType), EnableIfComparison.Equal, FogOfWarRenderType.Software), Tooltip("For software only. If enabled, can result in slightly smoother line of sight edges. This may have an impact on performance.")]
        public bool lineOfSightAntiAliasing = false;

        [Header("Performance")]
        [Tooltip("The internal render used to render the fog texture.\nSoftware: CPU-heavy. Ideal for small map resolutions (under 100), but will most likely result in slower performance.Hardware: GPU-heavy. Ideal for large map resolutions and/or large unit counts. This is the recommended renderer!")]
        public FogOfWarRenderType renderType = FogOfWarRenderType.Software;
        [EnableIf(nameof(renderType), EnableIfComparison.Equal, FogOfWarRenderType.Software), Tooltip("For software only. If enabled, the rendering will be done off of the main unity thread. This can result in better performance when there are large unit counts.")]
        public bool multithreaded = false;
        bool _isMultithreaded { get { return multithreaded && renderType == FogOfWarRenderType.Software; } }
        [EnableIf(nameof(renderType), EnableIfComparison.Equal, FogOfWarRenderType.Software), Range(2, 8), Tooltip("For multithreaded software only. This is the number of threads used to render FogOfWarUnits (including the main thread).")]
        public int threads = 2;
        [Tooltip("If the fog spends longer than this number of milliseconds updating the fog texture, it will delay more rendering until the next frame. Set this to a high number to ensure all of the fog is rendered in a single frame.")]
        public double maxMillisecondsPerFrame = 5;
        [Tooltip("If true, FogOfWarTeam.fogValues will be retrieved. In software, this is free. In hardware, this involves pulling the texture data from the GPU, which is very slow. This can improve the reliability when calling GetFogValues(). It is strongly recommended to leave this off.")]
        public bool cpuFogValues = false;
        FogOfWarThreadPool _threadPool = null;
        int _currentUnitProcessing = 0;
        float _timeSinceLastUpdate = 0;
        System.Diagnostics.Stopwatch _stopwatch = new System.Diagnostics.Stopwatch();
        FogOfWarBlur _blur = new FogOfWarBlur();

        // Core
        public Texture fogTexture { get; private set; } // R = Partial, G = Visible, B = Unused
        public Color32[] fogValues { get; private set; } = null; // R = Partial, G = Visible, B = Unused, A = Unused
        Color32[] _cachedFogValues = null;
        FogOfWarDrawer _drawer = null;
        int _drawThreadTaskPoolCount = 0;
        List<FogOfWarDrawThreadTask> _drawThreadTaskPool = new List<FogOfWarDrawThreadTask>();
        public UnityEngine.Events.UnityEvent onFogTextureChanged { get; private set; } = new UnityEngine.Events.UnityEvent(); // called when fogTexture or has changed
        public UnityEngine.Events.UnityEvent onRenderFogTexture { get; private set; } = new UnityEngine.Events.UnityEvent(); // only call SetFogValues() with multithreading when this is invoked!

        static List<FogOfWarTeam> _instances = new List<FogOfWarTeam>();
        public static List<FogOfWarTeam> instances { get { return _instances; } }

        /// <summary>
        /// Returns the FogOfWarTeam for a particular team.
        /// If no team exists, null will be returned.
        /// </summary>
        /// <param name="team">The index of the team to get.</param>
        /// <returns>The data for the team.</returns>
        public static FogOfWarTeam GetTeam(int team)
        {
            for (int i = 0; i < instances.Count; ++i)
            {
                if (instances[i].team == team)
                    return instances[i];
            }
            return null;
        }

        void Awake()
        {
            Reinitialize();
        }

        void OnEnable()
        {
            _instances.Add(this);
        }

        void OnDisable()
        {
            _instances.Remove(this);
        }

        void OnDestroy()
        {
            _blur?.Release();

            if (_drawer != null)
                _drawer.OnDestroy();

            if (_threadPool != null)
            {
                _threadPool.StopAllThreads();
                _threadPool = null;
            }
        }

        FogOfWarMap _cachedMap = null;

        /// <summary>
        /// Reinitializes fog texture. Call this if you have changed the mapSize, mapResolution or mapOffset during runtime.
        /// This will also reset the fog. You can manually call this from the editor by right-clicking the FogOfWar component.
        /// </summary>
        public void Reinitialize()
        {
            if (_drawer != null)
                _drawer.OnDestroy();
            if (renderType == FogOfWarRenderType.Software)
                _drawer = new FogOfWarDrawerSoftware();
            else if (renderType == FogOfWarRenderType.Hardware)
                _drawer = new FogOfWarDrawerHardware();
            if (_cachedMap == null)
                _cachedMap = new FogOfWarMap(this);
            else
                _cachedMap.Set(this);
            _drawer.Initialise(_cachedMap);
            _drawer.StartFrame();

            if (_cachedFogValues != null && _cachedFogValues.Length != mapPixelCount)
                _cachedFogValues = null;

            _drawThreadTaskPool.Clear();
        }

        /// <summary>
        /// Returns the current fog values.
        /// Null may be returned if a fog frame has not been rendererd.
        /// Returned values are 0 for completely unfogged, and 255 for completely fogged.
        /// The size of the array will be mapResolution * mapResolution.
        /// </summary>
        public byte[] GetFogValues(FogOfWarValueType type)
        {
            byte[] data = new byte[fogValues.Length];
            GetFogValues(type, data);
            return data;
        }

        /// <summary>
        /// Copies the current fog values into the specified array.
        /// Returned values are 0 for completely unfogged, and 255 for completely fogged.
        /// The size of the array should be mapResolution * mapResolution.
        /// </summary>
        /// <param name="values"></param>
        public void GetFogValues(FogOfWarValueType type, byte[] values)
        {
            Debug.Assert(values != null && values.Length == mapPixelCount);

            Color32[] source = fogValues;
            if (source == null)
            {
                if (_cachedFogValues == null)
                    _cachedFogValues = new Color32[mapPixelCount];

                _drawer.GetValues(_cachedFogValues);
                source = _cachedFogValues;
            }

            if (type == FogOfWarValueType.Visible)
            {
                for (int i = 0; i < source.Length; ++i)
                    values[i] = source[i].r;
            }
            else if (type == FogOfWarValueType.Partial)
            {
                for (int i = 0; i < source.Length; ++i)
                    values[i] = source[i].g;
            }
            else
                throw new System.ArgumentException("Invalid FogOfWarValueType.");
        }

        /// <summary>
        /// Copies the specified array into the current fog values.
        /// Values are 0 for completely unfogged, and 255 for completely fogged.
        /// The size of the array should be mapResolution * mapResolution.
        /// </summary>
        /// <param name="currentvalues"></param>
        public void SetFogValues(FogOfWarValueType type, byte[] values)
        {
            _drawer.SetValues(type, values);
        }
        
        /// <summary>
        /// Converts a world position to a fog pixel position. Values will be between 0 and mapResolution.
        /// </summary>
        /// <param name="position"></param>
        /// <returns></returns>
        public Vector2Int WorldPositionToFogPosition(Vector3 position)
        {
            Vector2 fogplanepos = FogOfWarConversion.WorldToFogPlane(position, plane);
            Vector2 mappos = FogOfWarConversion.WorldToFog(fogplanepos, mapOffset, mapResolution, mapSize);
            return mappos.ToInt();
        }

        /// <summary>
        /// Returns the total fog amount at a particular world position. 0 is fully unfogged and 255 if fully fogged.
        /// </summary>
        /// <param name="position"></param>
        /// <returns></returns>
        public byte GetFogValue(FogOfWarValueType type, Vector3 position)
        {
            if (fogValues != null)
            {
                Vector2Int mappos = WorldPositionToFogPosition(position);
                mappos.x = Mathf.Clamp(mappos.x, 0, mapResolution.x - 1);
                mappos.y = Mathf.Clamp(mappos.y, 0, mapResolution.y - 1);
                Color32 value = fogValues[mappos.y * mapResolution.x + mappos.x];

                if (type == FogOfWarValueType.Visible)
                    return value.r;
                else if (type == FogOfWarValueType.Partial)
                    return value.g;
                else
                    throw new System.ArgumentException("Invalid FogOfWarValueType.");
            }
            else
            {
                if (type == FogOfWarValueType.Partial)
                {
                    Debug.LogWarning("Cannot call FogOfWarTeam.GetFogValue() with FogOfWarValueType.Partial when cpuFogValues is set to false.");
                    return 255;
                }

                byte fog = 255;
                Vector2Int fogposition = WorldPositionToFogPosition(position);
                for (int i = 0; i < FogOfWarUnit.registeredUnits.Count; ++i)
                {
                    if (FogOfWarUnit.registeredUnits[i].team != team)
                        continue;

                    byte unitfog = FogOfWarUnit.registeredUnits[i].GetVisibility(this, _cachedMap, fogposition);
                    if (unitfog < fog)
                        fog = unitfog;
                }

                return fog;
            }
        }

        /// <summary>
        /// Set the fog for a square area of the map. Positions are all in world coordinates. 0 is fully unfogged and 255 if fully fogged.
        /// </summary>
        /// <param name="bounds"></param>
        /// <param name="value"></param>
        public void SetFog(FogOfWarValueType type, Bounds bounds, byte value)
        {
            Rect rect = new Rect();
            rect.min = FogOfWarConversion.WorldToFog(bounds.min, plane, mapOffset, mapResolution, mapSize);
            rect.max = FogOfWarConversion.WorldToFog(bounds.max, plane, mapOffset, mapResolution, mapSize);

            int xmin = (int)Mathf.Max(rect.xMin, 0);
            int xmax = (int)Mathf.Min(rect.xMax, mapResolution.x);
            int ymin = (int)Mathf.Max(rect.yMin, 0);
            int ymax = (int)Mathf.Min(rect.yMax, mapResolution.y);

            // if it is not visible on the map
            if (xmin >= mapResolution.x || xmax < 0 || ymin >= mapResolution.y || ymax < 0)
                return;

            _drawer.SetFog(type, new RectInt(xmin, ymin, xmax - xmin, ymax - ymin), value);
        }

        /// <summary>
        /// Sets the fog value for the entire map. Set to 0 for completely unfogged, to 255 for completely fogged.
        /// </summary>
        /// <param name="value"></param>
        public void SetAll(FogOfWarValueType type, byte value = 255)
        {
            _drawer.SetAll(type, value);
        }

        /// <summary>
        /// Checks the average visibility of an area. 0 is fully unfogged and 1 if fully fogged.
        /// </summary>
        /// <param name="worldbounds"></param>
        public float VisibilityOfArea(FogOfWarValueType type, Bounds worldbounds)
        {
            if (fogValues == null)
            {
                Debug.LogWarning("Cannot call FogOfWarTeam.VisibilityOfArea() when cpuFogValues is set to false.");
                return 1;
            }

            Vector2 min = FogOfWarConversion.WorldToFog(worldbounds.min, plane, mapOffset, mapResolution, mapSize);
            Vector2 max = FogOfWarConversion.WorldToFog(worldbounds.max, plane, mapOffset, mapResolution, mapSize);

            int xmin = Mathf.Clamp(Mathf.RoundToInt(min.x), 0, mapResolution.x);
            int xmax = Mathf.Clamp(Mathf.RoundToInt(max.x), 0, mapResolution.x);
            int ymin = Mathf.Clamp(Mathf.RoundToInt(min.y), 0, mapResolution.y);
            int ymax = Mathf.Clamp(Mathf.RoundToInt(max.y), 0, mapResolution.y);

            float total = 0;
            int count = 0;
            if (type == FogOfWarValueType.Visible)
            {
                for (int y = ymin; y < ymax; ++y)
                {
                    for (int x = xmin; x < xmax; ++x)
                    {
                        ++count;
                        total += fogValues[y * mapResolution.x + x].r / 255.0f;
                    }
                }
            }
            else if (type == FogOfWarValueType.Partial)
            {
                for (int y = ymin; y < ymax; ++y)
                {
                    for (int x = xmin; x < xmax; ++x)
                    {
                        ++count;
                        total += fogValues[y * mapResolution.x + x].g / 255.0f;
                    }
                }
            }
            else
                throw new System.ArgumentException("Invalid FogOfWarValueType.");

            return total / count;
        }

        /// <summary>
        /// Returns how much of the map has been explored/unfogged, where 0 is 0% and 1 is 100%.
        /// Increase the skip value to improve performance but sacrifice accuracy.
        /// </summary>
        /// <param name="skip"></param>
        /// <returns></returns>
        public float ExploredArea(int skip = 1)
        {
            if (fogValues == null)
            {
                Debug.LogWarning("Cannot call FogOfWarTeam.ExploredArea() when cpuFogValues is set to false.");
                return 1;
            }

            skip = Mathf.Max(skip, 1);
            int total = 0;
            for (int i = 0; i < fogValues.Length; i += skip)
                total += fogValues[i].g;
            return (1.0f - total / (fogValues.Length * 255.0f / skip)) * 2;
        }

        /// <summary>
        /// Returns a list of all of the units in a specific team that are visible to the current team.
        /// The threshold of visibility is specified with the maxFog value, where 0 is fully unfogged and 255 is fully fogged.
        /// The list of units will not be cleared when called and is assumed to be empty.
        /// </summary>
        /// <param name="teamindex"></param>
        /// <param name="maxfog"></param>
        /// <param name="units"></param>
        public void GetVisibleUnits(int teamindex, byte maxfog, List<FogOfWarUnit> units)
        {
            for (int i = 0; i < FogOfWarUnit.registeredUnits.Count; ++i)
            {
                FogOfWarUnit unit = FogOfWarUnit.registeredUnits[i];
                if (unit.team == teamindex && GetFogValue(FogOfWarValueType.Visible, unit.transform.position) < maxfog)
                    units.Add(unit);
            }
        }

        void ProcessUnits(System.Diagnostics.Stopwatch stopwatch)
        {
            // if we are not updating units and all units have finished processing
            if (!updateUnits && _currentUnitProcessing >= FogOfWarUnit.registeredUnits.Count)
                return;

            // remove any invalid units
            FogOfWarUnit.registeredUnits.RemoveAll(u => u == null);

            double millisecondfrequency = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            for (; _currentUnitProcessing < FogOfWarUnit.registeredUnits.Count; ++_currentUnitProcessing)
            {
                if (!FogOfWarUnit.registeredUnits[_currentUnitProcessing].isActiveAndEnabled || FogOfWarUnit.registeredUnits[_currentUnitProcessing].team != team)
                    continue;

                FogOfWarShape shape = FogOfWarUnit.registeredUnits[_currentUnitProcessing].GetShape(this);
                if (_isMultithreaded)
                {
                    ++_drawThreadTaskPoolCount;
                    while (_drawThreadTaskPoolCount > _drawThreadTaskPool.Count)
                        _drawThreadTaskPool.Add(new FogOfWarDrawThreadTask());

                    FogOfWarDrawThreadTask task = _drawThreadTaskPool[_drawThreadTaskPoolCount - 1];
                    task.drawer = _drawer;
                    task.shape = shape;
                    _threadPool.Run(task);
                }
                else
                    _drawer.Draw(shape, false);

                // do the timer check here so that at least one unit will be processed
                if (stopwatch != null && _stopwatch.ElapsedTicks * millisecondfrequency >= maxMillisecondsPerFrame)
                {
                    ++_currentUnitProcessing;
                    break;
                }
            }
        }

        /// <summary>
        /// Forces the fog to update. This should only be called when updateAutomatically is false. You can manually call this from the editor by right-clicking the FogOfWar component.
        /// </summary>
        /// <param name="timesincelastupdate"></param>
        public void ManualUpdate(float timesincelastupdate)
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("Cannot do manual update when not playing!", this);
                return;
            }

            if (updateAutomatically && updateUnits)
            {
                Debug.LogWarning("Cannot do manual update when both updateAutomatically and updateMode are true!", this);
                return;
            }

            if (_isPerformingManualUpdate)
                return;

            _currentUnitProcessing = 0;
            _isPerformingManualUpdate = true; // flag for only one draw
            if (!updateUnits)
                _drawer.StartFrame();

            // multithreading will update on the next Update(), but single thread can do it now
            if (!_isMultithreaded)
            {
                ProcessUnits(null);
                CompileTextures(ref timesincelastupdate, false);
            }
        }

        void Update()
        {
            if (!updateAutomatically && !_isPerformingManualUpdate)
                return;

            // prepare threads
            if (_isMultithreaded)
            {
                if (_threadPool == null)
                    _threadPool = new FogOfWarThreadPool();

                // do some thread maintenance
                threads = Mathf.Clamp(threads, 2, 8);
                _threadPool.maxThreads = threads;
                _threadPool.Clean();
            }
            else if (_threadPool != null)
            {
                _threadPool.StopAllThreads();
                _threadPool = null;
            }

            _stopwatch.Reset();
            _stopwatch.Start();

            // draw unit shapes
            ProcessUnits(_stopwatch);

            // compile final texture
            _timeSinceLastUpdate += Time.deltaTime;
            CompileTextures(ref _timeSinceLastUpdate, true);

            _stopwatch.Stop();
        }

        void CompileTextures(ref float timesincelastupdate, bool checkstopwatch)
        {
            // don't compile until all units have been processed
            if (_currentUnitProcessing < FogOfWarUnit.registeredUnits.Count || (checkstopwatch && _isMultithreaded && !_threadPool.hasAllFinished))
                return;

            _drawer.Combine(doFade, Mathf.Clamp01(timesincelastupdate / fadeDuration));

            // get the fog values from the drawer
            // get current values from units (if updateUnits is false, this will retain what it have since the last time updateUnits was true)
            if (cpuFogValues)
            {
                if (fogValues == null || fogValues.Length != mapPixelCount)
                    fogValues = new Color32[mapPixelCount];

                for (int i = 0; i < fogValues.Length; ++i)
                    fogValues[i] = new Color32(255, 255, 0, 0);

                _drawer.GetValues(fogValues);
            }
            else
                fogValues = null;

            if (outputToTexture)
            {
                Texture newoutput = _drawer.EndFrame();

                newoutput = _blur.Apply(newoutput, mapResolution, blurType, blurIterations);

                if (newoutput != fogTexture)
                {
                    fogTexture = newoutput;
                    newoutput.wrapMode = TextureWrapMode.Clamp;
                    newoutput.filterMode = FilterMode.Point;
                    onFogTextureChanged?.Invoke();
                }

                onRenderFogTexture.Invoke();
            }

            // start next fog frame
            if (updateUnits)
            {
                _drawer.StartFrame();
                _currentUnitProcessing = 0;
            }
            timesincelastupdate = 0;
            _drawThreadTaskPoolCount = 0;
            _isPerformingManualUpdate = false; // manual update has finished
        }

        /// <summary>
        /// Applies all properties to the specified material that are required to detect fog for this team.
        /// See CustomFogShader.shader for more info.
        /// </summary>
        public void ApplyToMaterial(Material material, float outsidefogstrength = 1)
        {
            FoWIDs ids = FoWIDs.instance;

            material.SetTexture(ids.fogTex, fogTexture);
            material.SetVector(ids.fogTextureSize, mapResolution.ToFloat());
            material.SetFloat(ids.mapSize, mapSize);
            material.SetVector(ids.mapOffset, mapOffset);
            material.SetFloat(ids.outsideFogStrength, outsidefogstrength);

            // which plane will the fog be rendered to?
            material.SetKeywordEnabled("PLANE_XY", plane == FogOfWarPlane.XY);
            material.SetKeywordEnabled("PLANE_YZ", plane == FogOfWarPlane.YZ);
            material.SetKeywordEnabled("PLANE_XZ", plane == FogOfWarPlane.XZ);
        }

        void OnDrawGizmosSelected()
        {
            fadeDuration = Mathf.Max(0, fadeDuration);

            Gizmos.color = Color.blue;
            if (plane == FogOfWarPlane.XY)
                Gizmos.DrawWireCube(new Vector3(mapOffset.x, mapOffset.y, 0), new Vector3(mapSize, mapSize, 0));
            else if(plane == FogOfWarPlane.XZ)
                Gizmos.DrawWireCube(new Vector3(mapOffset.x, 0, mapOffset.y), new Vector3(mapSize, 0, mapSize));
            else if (plane == FogOfWarPlane.YZ)
                Gizmos.DrawWireCube(new Vector3(0, mapOffset.x, mapOffset.y), new Vector3(0, mapSize, mapSize));
        }
    }
}
