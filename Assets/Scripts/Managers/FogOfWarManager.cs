using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[DefaultExecutionOrder(-100)]
public class FogOfWarManager : MonoBehaviour
{
    [System.Serializable]
    public class VisionGrid
    {
        // the width and height of the grid (needed to access the arrays)
        public Vector2Int size { get; private set; }

        // array of size width * height, each entry has an int with the 
        // bits representing which players have this entry in vision.
        private byte[] values = null;

        // similar to the values but it just stores if a player visited
        // that entry at some point in time.
        //private byte[] visited = null;

        public VisionGrid(Vector2Int gridSize)
        {
            size = gridSize;
            values = new byte[size.x * size.y];
            //visited = new byte[size.x * size.y];
        }

        public void SetVisible(Vector2Int pos, FactionTemplate.PlayerID players, bool value)
        {
            if (value)
            {

                values[pos.x + pos.y * size.y] |= (byte)players;
                //visited[pos.x + pos.y * size.y] |= (byte)players;
            }
            else
            {
                players = ~players;
                values[pos.x + pos.y * size.y] ^= (byte)players;
            }
        }

        public void Clear()
        {
            if (values != null)
            {
                System.Array.Clear(values, 0, size.x * size.y);
            }
        }

        public bool IsVisible(int index, FactionTemplate.PlayerID players)
        {
            return (values[index] & (byte)players) > 0;
        }

        public bool IsVisible(Vector2Int pos, FactionTemplate.PlayerID players)
        {
            return (values[pos.x + pos.y * size.y] & (byte)players) > 0;
        }

        /*
        public bool WasVisible(int index, FactionTemplate.PlayerID players)
        {
            return (visited[index] & (byte)players) > 0;
        }

        public bool WasVisible(Vector2Int pos, FactionTemplate.PlayerID players)
        {
            return (visited[pos.x + pos.y * size.y] & (int)players) > 0;
        }
        */
    }

    public class Terrain
    {
        // the width and height of the grid (needed to access the arrays) 
        public Vector2Int size { get; private set; }

        // array of size width * height, has the terrain level of the 
        // grid entry. 
        private byte[] height;

        public Terrain(Vector2Int gridSize, Vector3 unityPos)
        {
            size = gridSize;
            CreateMap(unityPos);
        }

        private void CreateMap(Vector3 unityPos)
        {
            LayerMask layerMask = new LayerMask().Add("Terrain");
            float heightOffset = unityPos.y;
            int length = size.x * size.y;
            height = new byte[length];
            Vector2Int gridOffset = new Vector2Int(Mathf.RoundToInt(unityPos.x), Mathf.RoundToInt(unityPos.z));
            for (int i = 0; i < length; i++)
            {
                int x = i % size.x;
                int y = i / size.x;
                Ray ray = new Ray(new Vector3(x + 0.5f + gridOffset.x, 24f, y + 0.5f + gridOffset.y), Vector3.down);
                RaycastHit hit;
                if (Physics.Raycast(ray, out hit, 32f, layerMask, QueryTriggerInteraction.Ignore))
                {
                    height[i] = (byte)Mathf.RoundToInt(hit.point.y);
                }
            }
        }

        public int GetHeight(int i)
        {
            return height[i];
        }

        public int GetHeight(Vector2Int pos)
        {
            if (!pos.InBounds(size))
            {
                throw new System.ArgumentOutOfRangeException(size.ToString());
            }
            return height[pos.x + pos.y * size.y];
        }
    }

    [System.Serializable]
    public class GridTreeCell
    {
        public Vector2Int localPos;// { get; private set; }
        public GridTreeCell parent { get; private set; }
        [SerializeField]
        private List<GridTreeCell> branchedOffCells;

        public GridTreeCell(Vector2Int pos, GridTreeCell parentCell = null)
        {
            localPos = pos;
            branchedOffCells = new List<GridTreeCell>();
            parent = parentCell;
            parentCell?.AddBranchCell(this);
        }

        public void AddBranchCell(GridTreeCell branchCell)
        {
            branchedOffCells.Add(branchCell);
        }

        public bool ContainsChildAt(Vector2Int pos)
        {
            return pos == localPos || branchedOffCells.Exists(cell => cell.localPos == pos);// || parent != null && parent.ContainsChildAt(pos);
        }

        public List<GridTreeCell> GetChildren()
        {
            return branchedOffCells;
        }
    }

    public Vector2Int gridSize = new Vector2Int(128, 128);
    private VisionGrid visionGrid;
    private Terrain terrainGrid;
    private RectInt gridBounds;

    private Texture2D texture;
    private Color[] fadeouts;
    private Color[] targets;
    private List<int> activeList = new List<int>();
    private BitArray activeCells;

    public Material material;
    public Projector projector;
    private Color fowUnexplored = Color.black;
    private Color fowNotViewed = Color.black.ToWithA(0.6f);
    public float decline = 1f;

    [Space]

    public bool drawGrid;
    public bool drawHeightValues;
    public Mesh drawQuadMesh;

    private static System.Comparison<Vector2Int> signedAngleComparison = new System.Comparison<Vector2Int>(Vector_Extension.SignedAngle);

    [Space]

    public RegisterObject units;
    public RegisterObject buildings;
    private List<ClickableObject> queueCurrent = new List<ClickableObject>();
    private int queueIndex = 0;
    public int perFrame = 10;

    public FactionTemplate.PlayerID activePlayers;

    private float lastCheck;

    public Vector2Int gridOffset
    {
        get
        {
            return new Vector2Int(Mathf.RoundToInt(transform.position.x), Mathf.RoundToInt(transform.position.z));
        }
    }

    public Dictionary<int, GridTreeCell> trees = new Dictionary<int, GridTreeCell>();

    void Start()
    {
        activePlayers = GameManager.Instance.playerFaction.data.playerID;

        gridBounds = new RectInt(Vector2Int.zero, gridSize);
        visionGrid = new VisionGrid(gridSize);
        SetTerrain();
        int length = gridSize.x * gridSize.y;

        fadeouts = new Color[length];
        targets = new Color[length];
        for (int i = 0; i < length; i++)
        {
            fadeouts[i] = Color.black;
            targets[i] = Color.clear;
        }
        activeCells = new BitArray(length, false);

        texture = new Texture2D(gridSize.x, gridSize.y);
        texture.name = "FOW grid";
        material.SetTexture("_MainTex", texture);
        projector.material = material;
    }

    void OnEnable()
    {
        units.onAdded += OnUnitAdd;
        buildings.onAdded += OnUnitAdd;
    }

    void OnDisable()
    {
        units.onRemoved += OnUnitRemove;
        buildings.onRemoved += OnUnitRemove;
    }

    void OnDrawGizmos()
    {
        if (drawGrid)
        {
            float heightOffset = transform.position.y;

            UnityEditor.Handles.color = Color.grey;
            for (int x = 0; x <= gridSize.x; x++)
            {
                UnityEditor.Handles.DrawAAPolyLine(new Vector3(x + gridOffset.x, heightOffset, 0 + gridOffset.y), new Vector3(x + gridOffset.x, heightOffset, gridSize.y + gridOffset.y));
            }
            for (int y = 0; y <= gridSize.y; y++)
            {
                UnityEditor.Handles.DrawAAPolyLine(new Vector3(0f + gridOffset.x, heightOffset, y + gridOffset.y), new Vector3(gridSize.x + gridOffset.x, heightOffset, y + gridOffset.y));
            }
        }
        if (drawHeightValues && terrainGrid != null)
        {
            float heightOffset = transform.position.y;

            GUIStyle style = new GUIStyle();
            style.fontSize = 20;
            int length = terrainGrid.size.x * terrainGrid.size.y;
            Camera cam = UnityEditor.SceneView.currentDrawingSceneView != null ? UnityEditor.SceneView.currentDrawingSceneView.camera : Camera.current;
            for (int i = 0; i < length; i++)
            {
                int height = terrainGrid.GetHeight(i);
                int x = i % gridSize.x;
                int y = i / gridSize.x;
                Vector3 point = new Vector3(x + 0.5f + gridOffset.x, heightOffset, y + 0.5f + gridOffset.y);
                Vector3 tmp = cam.WorldToViewportPoint(point);
                if (tmp.x.IsPercent() && tmp.y.IsPercent() && tmp.z > 0f && Vector3.Distance(cam.transform.position, point) < 30f)
                {
                    style.normal.textColor = Color.Lerp(Color.cyan, Color.blue, ((float)height).LinearRemap(0, 10));
                    UnityEditor.Handles.Label(point, height.ToString(), style);
                }
            }
        }
    }

    void OnDrawGizmosSelected()
    {
        /*
        if (visionGrid != null)
        {
            int gridLenght = gridSize.x * gridSize.y;
            FactionTemplate.PlayerID playerId = GameManager.Instance.playerFaction.data.playerID;
            for (int i = 0; i < gridLenght; i++)
            {
                int x = i % gridSize.x;
                int y = i / gridSize.x;
                if (visionGrid.IsVisible(new Vector2Int(x, y), playerId))
                {
                    Gizmos.color = Color.cyan;
                    Gizmos.DrawCube(GridPosToUnityPos(new Vector2Int(x, y)), (Vector3.one * 0.9f).ToWithY(0.001f));
                }
            }
        }
        */

        /*
        int radius = 10;
        List<Vector2Int> circle = GetCirclePositions(Vector2Int.zero, radius);
        Gizmos.color = Color.blue;
        foreach (Vector2Int pos in circle)
        {
            Gizmos.color = Color.blue;
            Gizmos.DrawCube(GridPosToUnityPos(pos), (Vector3.one * 0.9f).ToWithY(0.001f));
        }
        Gizmos.color = Random.ColorHSV();
        Vector2Int rnd = circle[Random.Range(0, circle.Count)];
        List<Vector2Int> line = GetLinePositions(Vector2Int.zero, rnd);
        foreach (Vector2Int pos in line)
        {
            Gizmos.DrawCube(GridPosToUnityPos(pos), (Vector3.one * 0.9f).ToWithY(0.001f));
        }
        Gizmos.color = Color.green;
        Gizmos.DrawCube(GridPosToUnityPos(line[0]), (Vector3.one * 0.9f).ToWithY(0.001f));
        Gizmos.color = Color.yellow;
        Gizmos.DrawCube(GridPosToUnityPos(line[line.Count - 1]), (Vector3.one * 0.9f).ToWithY(0.001f));
        */
    }

    void LateUpdate()
    {
        if (Time.realtimeSinceStartup - lastCheck < 0.2f)
        {
            //return;
        }
        bool fullIteration = CalculateVision();

        DrawVision();
        if (fullIteration)
        {
            lastCheck = Time.realtimeSinceStartup;
        }
    }

    [ContextMenu("SetTerrain")]
    private void SetTerrain()
    {
        terrainGrid = new Terrain(gridSize, transform.position);
    }

    private bool CalculateVision()
    {
        if (queueIndex == 0)
        {
            //visionGrid.Clear();
        }

        int length = queueCurrent.Count;
        for (int i = queueIndex; i < queueIndex + perFrame; i++)
        {
            if (i == length)
            {
                queueIndex = 0;
                return true;
            }
            ClickableObject unit = queueCurrent[i];
            Vector2Int position = UnityPosToGridPos(unit.transform.position);
            int terrainHeight = terrainGrid.GetHeight(position);
            int visionRange = Mathf.RoundToInt(unit.template.guardDistance);
            FactionTemplate.PlayerID playerID = unit.faction.data.playerID;

            GridTreeCell rootCell = null;
            if (!trees.TryGetValue(visionRange, out rootCell))
            {
                rootCell = CreateGridSelectionTree(visionRange);
                trees.Add(visionRange, rootCell);
            }
            CheckVisionWithTree(rootCell, position, terrainHeight, playerID);
        }
        queueIndex += perFrame;
        return false;
    }

    public void CheckVisionWithTree(GridTreeCell cell, Vector2Int offset, int terrainHeight, FactionTemplate.PlayerID players)
    {
        Vector2Int pos = cell.localPos + offset;
        if (!gridBounds.Contains(pos))
        {
            return;
        }
        int index = pos.ToIndex(gridSize.y);
        if (terrainGrid.GetHeight(pos) > terrainHeight)
        {
            activeCells.Set(index, false);
            visionGrid.SetVisible(pos, players, false);
            return;
        }

        visionGrid.SetVisible(pos, players, true);
        if (!activeCells.Get(index))
        {
            activeCells.Set(index, true);
            activeList.Add(index);
        }
        fadeouts[index].a = 100f / 255f;

        List<GridTreeCell> children = cell.GetChildren();
        foreach (GridTreeCell childCell in children)
        {
            CheckVisionWithTree(childCell, offset, terrainHeight, players);
        }
    }

    [ContextMenu("CreateGridSelectionTree")]
    public GridTreeCell CreateGridSelectionTree(int radius)
    {
        List<Vector2Int> circle = GetCirclePositions(Vector2Int.zero, radius);
        int startLenght = circle.Count;
        for (int i = 0; i < startLenght; i++)
        {
            int j = (i + 1) % startLenght;
            List<Vector2Int> tmp = GetLinePositions(circle[i], circle[j]);

            foreach (Vector2Int lineOnCirclePos in tmp)
            {
                if (!circle.Contains(lineOnCirclePos))
                {
                    circle.Add(lineOnCirclePos);
                }
            }
        }
        List<List<Vector2Int>> lines = new List<List<Vector2Int>>();
        foreach (var outlinePos in circle)
        {
            List<Vector2Int> line = GetLinePositions(Vector2Int.zero, outlinePos);
            line.RemoveAt(0);
            if (line.Count > 0)
            {
                lines.Add(line);
            }
        }

        GridTreeCell rootCell = new GridTreeCell(Vector2Int.zero);
        AddGridSelectionTreeLevel(lines, rootCell, 0);
        return rootCell;
    }

    private void AddGridSelectionTreeLevel(List<List<Vector2Int>> linePointLists, GridTreeCell cell, int level)
    {
        List<KeyValuePair<Vector2Int, List<List<Vector2Int>>>> firstPointDict = new List<KeyValuePair<Vector2Int, List<List<Vector2Int>>>>();
        for (int i = linePointLists.Count - 1; i >= 0; i--)
        {
            List<Vector2Int> line = linePointLists[i];
            Vector2Int linePos = line[0];

            List<List<Vector2Int>> lines = firstPointDict.Find(pair => pair.Key == linePos).Value;
            if (lines == null || lines.Count == 0)
            {
                lines = new List<List<Vector2Int>>();
                firstPointDict.Add(new KeyValuePair<Vector2Int, List<List<Vector2Int>>>(linePos, lines));
            }

            line.RemoveAt(0);
            linePointLists.RemoveAt(i);
            if (line.Count > 0)
            {
                lines.Add(line);
            }
        }

        for (int i = 0; i < firstPointDict.Count; i++)
        {
            Vector2Int linePos = firstPointDict[i].Key;
            List<List<Vector2Int>> lines = firstPointDict[i].Value;
            GridTreeCell brachCell = new GridTreeCell(linePos, cell);

            AddGridSelectionTreeLevel(lines, brachCell, level + 1);
        }
    }

    private void DrawVision()
    {
        /*
        int length = gridSize.x * gridSize.y;
        for (int i = 0; i < length; i++)
        {
            if (colors[i] != fowUnexplored)
            {
                colors[i] = fowUnexplored;
            }
            if (visionGrid.IsVisible(i, activePlayers))
            {
                colors[i] = Color.clear;
            }
            else if (visionGrid.WasVisible(i, activePlayers))
            {
                colors[i] = fowNotViewed;
            }
        }
        */

        for (int i = 0, j = activeList.Count - 1; j >= 0; j--)
        {
            i = activeList[j];
            Color fadeout = fadeouts[i];
            if (fadeout.a > decline)
            {
                fadeout.a -= decline;
                fadeouts[i] = fadeout;
                continue;
            }

            Color target = targets[i];
            target.a += decline;
            if (fadeout.a >= fowNotViewed.a)
            {
                fadeouts[i] = fowNotViewed;
                activeCells.Set(i, false);

                activeList[j] = activeList[activeList.Count - 1];
                activeList.RemoveAt(activeList.Count - 1);
            }
        }

        texture.SetPixels(targets);
        texture.Apply();
    }

    public List<Vector2Int> GetCirclePositions(Vector2Int center, int radius)
    {
        List<Vector2Int> circle = new List<Vector2Int>();
        int x = 0;
        int y = radius;
        int d = 3 - 2 * radius;
        do
        {
            Vector2Int p;
            p = new Vector2Int(center.x + x, center.y + y);
            if (!circle.Contains(p))
            {
                circle.Add(p);
            }
            p = new Vector2Int(center.x - x, center.y + y);
            if (!circle.Contains(p))
            {
                circle.Add(p);
            }
            p = new Vector2Int(center.x + x, center.y - y);
            if (!circle.Contains(p))
            {
                circle.Add(p);
            }
            p = new Vector2Int(center.x - x, center.y - y);
            if (!circle.Contains(p))
            {
                circle.Add(p);
            }
            p = new Vector2Int(center.x + y, center.y + x);
            if (!circle.Contains(p))
            {
                circle.Add(p);
            }
            p = new Vector2Int(center.x - y, center.y + x);
            if (!circle.Contains(p))
            {
                circle.Add(p);
            }
            p = new Vector2Int(center.x + y, center.y - x);
            if (!circle.Contains(p))
            {
                circle.Add(p);
            }
            p = new Vector2Int(center.x - y, center.y - x);
            if (!circle.Contains(p))
            {
                circle.Add(p);
            }

            x++;
            if (d > 0)
            {
                y--;
                d = d + 4 * (x - y) + 10;
            }
            else
            {
                d = d + 4 * x + 6;
            }
        } while (y >= x);

        //circle = circle.OrderBy(pos => Vector2.SignedAngle(pos, Vector2.right) + 180f).ToList();
        circle.Sort(signedAngleComparison);
        int startLenght = circle.Count;
        for (int i = 0; i < startLenght; i++)
        {
            int j = (i + 1) % startLenght;
            List<Vector2Int> tmp = GetLinePositions(circle[i], circle[j]);
            foreach (Vector2Int lineOnCirclePos in tmp)
            {
                if (!circle.Contains(lineOnCirclePos))
                {
                    circle.Add(lineOnCirclePos);
                }
            }
        }

        return circle;
    }

    private List<Vector2Int> GetLinePositions(Vector2Int p0, Vector2Int p1)
    {
        int dx = p1.x - p0.x;
        int dy = p1.y - p0.y;
        int nx = Mathf.Abs(dx);
        int ny = Mathf.Abs(dy);
        int sign_x = dx > 0 ? 1 : -1;
        int sign_y = dy > 0 ? 1 : -1;

        Vector2Int p = p0;
        List<Vector2Int> line = new List<Vector2Int> { p };

        for (int ix = 0, iy = 0; ix < nx || iy < ny;)
        {
            if (dx.Sign() != dy.Sign())
            {
                if ((2 * ix + 1) * ny < (2 * iy + 1) * nx)
                {
                    p.x += sign_x;
                    ix++;
                }
                else
                {
                    p.y += sign_y;
                    iy++;
                }
            }
            else
            {
                if ((2 * ix + 0) * ny < (2 * iy + 1) * nx)
                {
                    p.x += sign_x;
                    ix++;
                }
                else
                {
                    p.y += sign_y;
                    iy++;
                }
            }
            line.Add(p);
        }
        return line;
    }

    private Vector2Int UnityPosToGridPos(Vector3 position)
    {
        Vector2Int gridPos = new Vector2Int(Mathf.FloorToInt(position.x), Mathf.FloorToInt(position.z)) - gridOffset;
        return gridPos;
    }

    private Vector3 GridPosToUnityPos(Vector2Int gridPos)
    {
        gridPos += gridOffset;
        Vector3 position = new Vector3(gridPos.x + 0.5f, transform.position.y, gridPos.y + 0.5f);
        return position;
    }

    public void OnUnitAdd(MonoBehaviour monoBehaviour, System.Type type)
    {
        if (monoBehaviour is ClickableObject)
        {
            ClickableObject clickableObject = monoBehaviour as ClickableObject;
            queueCurrent.Add(clickableObject);
        }
        else
        {
            throw new System.ArrayTypeMismatchException();
        }
    }

    public void OnUnitRemove(MonoBehaviour monoBehaviour, System.Type type)
    {
        if (monoBehaviour is ClickableObject)
        {
            ClickableObject clickableObject = monoBehaviour as ClickableObject;
            queueCurrent.Remove(clickableObject);
            queueIndex--;
        }
        else
        {
            throw new System.ArrayTypeMismatchException();
        }
    }
}
