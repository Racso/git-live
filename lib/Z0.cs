using System.Text.RegularExpressions;

namespace Racso.Z0.Parser;

/*
    Example of using Z0 parser:
    
    var data = Z0.Parse("file-content-goes-here").AsZNode(); // ZNode is a read-only wrapper that provides convenient accessors.
    string name = data["name"]; //automatic conversion to string; same as .Optional() == .Optional("")
    string address = data["address"].Optional("?"); //optional value with fallback
    string required = data["token"].Required(); //throws if missing
    
    foreach (var book in data["array-of-books"]) //iterating an array
    {
        Console.WriteLine(book); //automatic conversion to string
    }

    All values are strings. If you need other types, convert them manually.
    You can also iterate arrays and dictionary keys, obtain array count, etc.
    
    Example Z0 format:
    // This is a comment
    name = John Doe
    address = 123 Main St
    
    array-of-books:
    # = The Great Gatsby
    # = 1984
*/

public enum ArrayType
{
    None,
    Unknown,
    Value,
    Dictionary
}

public class ParsingNode
{
    public string Path { get; }
    public ParsingNode? Parent { get; }
    private readonly Dictionary<string, ParsingNode> children = new(EqualityComparer<string>.Create(Z0ParserMiniExtensions.KeyEquals, Z0ParserMiniExtensions.KeyHashCode));
    public IReadOnlyDictionary<string, ParsingNode> GetChildren() => !IsValue ? children : throw new InvalidOperationException($"Node '{Path}' does not have children.");
    public int ChildCount => children.Count;


    public ArrayType ArrayType { get; private set; }
    public bool IsArray => ArrayType != ArrayType.None;

    public string Value => value ?? throw new InvalidOperationException($"Node '{Path}' does not have a scalar value.");
    public bool IsValue => value != null;
    private string? value;

    public static ParsingNode NewChild(ParsingNode parent, string part)
    {
        ParsingNode newNode = new(parent, part);
        parent.AddChild(newNode, part);
        return newNode;
    }

    public static ParsingNode NewRootNode()
        => new(null, "");

    private ParsingNode(ParsingNode? parent, string part)
    {
        Path = parent == null || parent.Path == "" ? part : parent.Path + "." + part;
        Parent = parent;
    }

    public void SetValue(string newValue)
    {
        if (IsValue)
            throw new Z0ParserException($"Node '{Path}' already has a value assigned.");
        if (IsArray)
            throw new Z0ParserException($"Cannot assign a value to array node '{Path}'.");
        if (children.Count > 0)
            throw new Z0ParserException($"Cannot assign a value to object node '{Path}'.");

        value = newValue;
    }

    public void SetAsArray(ArrayType type)
    {
        if (type == ArrayType.None)
            throw new ArgumentException("Kind cannot be None when setting as array.");
        if (type == ArrayType)
            return;

        if (ArrayType is ArrayType.None or ArrayType.Unknown)
            ArrayType = type;
        else
            throw new Z0ParserException($"Cannot change array kind from {ArrayType} to {type} for node '{Path}'.");
    }

    public void AddChild(ParsingNode child, string part)
    {
        if (IsValue)
            throw new Z0ParserException($"Cannot add child to value node '{Path}'.");
        if (children.ContainsKey(part))
            throw new Z0ParserException($"Node '{Path}' already has a child with key '{part}'.");
        if (IsArray && !int.TryParse(part, out _))
            throw new Z0ParserException($"Cannot add non-numeric child '{part}' to array node '{Path}'.");

        children[part] = child;
    }

    public bool TryGetChild(string part, out ParsingNode? child)
    {
        if (IsValue)
            throw new Z0ParserException($"Cannot get child from value node '{Path}'.");

        return children.TryGetValue(part, out child);
    }
}

public class Parser
{
    private readonly IEnumerable<string> lines;
    private readonly ParsingNode root;
    private readonly HashSet<ParsingNode> lockedNodes = new(32);

    private string currentSection = "";
    private ParsingNode currentNode;
    private string CurrentNodePath => currentNode.Path;
    private static readonly Regex KeyRegex = new("^(?:[a-zA-Z_-][a-zA-Z0-9_-]*|[0-9]+)$", RegexOptions.Compiled);
    private bool parsed;

    public Parser(IEnumerable<string> lines)
    {
        this.lines = lines;
        root = ParsingNode.NewRootNode();
        currentNode = root;
    }

    public ParsingNode Parse()
    {
        if (parsed)
            return root;

        parsed = true;
        int currentLine = 0;

        foreach (string rawLine in lines)
        {
            currentLine++;
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("//"))
                continue;

            try
            {
                if (line.Contains('='))
                {
                    ApplyAssignment(line);
                }
                else if (line.EndsWith(':'))
                {
                    MoveToNode(line[..^1].Trim());
                    currentSection = CurrentNodePath;
                }
                else
                {
                    throw new Z0ParserException($"Unrecognized line format: '{line}'");
                }
            }
            catch (Z0ParserException ex)
            {
                throw new Z0ParserException(currentLine, ex.Message);
            }
        }

        return root;
    }

    private void ApplyAssignment(string line)
    {
        string[] split = line.Split('=', 2);
        if (split.Length != 2)
            throw new Z0ParserException("Assignment must contain '='.");

        string left = split[0].Trim();
        string value = split[1].Trim();
        if (string.IsNullOrEmpty(left))
            throw new Z0ParserException("Assignment path cannot be empty.");

        string[] leftParts = left.Split('.', 2);
        string[] sectionParts = currentSection.Split('.', 2);
        if (leftParts.Length > 0 && sectionParts.Length > 0 && Z0ParserMiniExtensions.KeyEquals(leftParts[0], sectionParts[0]))
            throw new Z0ParserException($"Key '{left}' and current section '{currentSection}' will produce cyclical-looking path '{currentSection}.{left}', which is probably a mistake. If you really want to do this, considering creating section '{currentSection}.{leftParts[0]}' (or an equivalent) explicitly.");

        string fullPath = currentSection == "" ? left : currentSection + "." + left;
        ValidateFullPath(fullPath);
        MoveToNode(fullPath);

        TryEnsureParentArray(currentNode, ArrayType.Value);
        currentNode.SetValue(value);
    }

    private void MoveToNode(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new Z0ParserException("Path cannot be empty.");

        string[] currentParts = CurrentNodePath == "" ? [] : CurrentNodePath.Split('.');
        string[] newParts = path.Split('.');
        int commonParts = 0;

        int minLength = Math.Min(currentParts.Length, newParts.Length);

        for (int i = 0; i < minLength; i++)
        {
            bool isExactMatch = Z0ParserMiniExtensions.KeyEquals(currentParts[i], newParts[i]);
            isExactMatch = isExactMatch || newParts[i] == "#" && int.TryParse(currentParts[i], out _);

            if (isExactMatch)
                commonParts++;
            else
                break;
        }

        // Special case: if we're adding a new item to an array, we need to go up one level, forced.
        if (commonParts == newParts.Length && newParts[^1] == "#")
            commonParts -= 1;

        int partsToGoUp = currentParts.Length - commonParts;

        for (int i = 0; i < partsToGoUp; i++)
        {
            lockedNodes.Add(currentNode);
            currentNode = currentNode.Parent ?? throw new Z0ParserException("Cannot move up from root node.");
        }

        for (int i = commonParts; i < newParts.Length; i++)
        {
            string part = newParts[i];
            ValidateSegment(part);

            if (part == "#")
            {
                if (!currentNode.IsArray && currentNode.ChildCount > 0)
                    throw new Z0ParserException($"Cannot add array element to object node '{currentNode.Path}'.");

                if (!currentNode.IsArray)
                    currentNode.SetAsArray(ArrayType.Unknown);
                part = currentNode.ChildCount.ToString();
            }
            else if (currentNode.IsArray)
            {
                throw new Z0ParserException($"Cannot create element '{part}' directly in array node '{currentNode.Path}'.");
            }

            if (currentNode.TryGetChild(part, out ParsingNode? existing))
            {
                if (existing == null)
                    throw new Z0ParserException($"Assertion failed! {nameof(currentNode.TryGetChild)} returned true but output child is null.");

                if (lockedNodes.Contains(existing))
                    throw new Z0ParserException($"Node '{existing.Path}' was already parsed. Cannot modify it.");

                currentNode = existing;
            }
            else
            {
                TryEnsureParentArray(currentNode, ArrayType.Dictionary);
                ParsingNode newNode = ParsingNode.NewChild(currentNode, part);
                currentNode = newNode;
            }
        }
    }

    private void TryEnsureParentArray(ParsingNode? node, ArrayType type)
    {
        if (node is not { Parent.IsArray: true })
            return;

        node.Parent.SetAsArray(type);
    }

    private void ValidateFullPath(string path)
    {
        if (path.StartsWith('.') || path.EndsWith('.'))
            throw new Z0ParserException("Path cannot start or end with a dot.");
        if (path.Contains(".."))
            throw new Z0ParserException("Consecutive dots are not allowed.");
        foreach (string seg in path.Split('.'))
        {
            if (seg == "")
                throw new Z0ParserException("Empty path segment is not allowed.");
            if (seg != "#" && !KeyRegex.IsMatch(seg))
                throw new Z0ParserException($"Invalid key segment '{seg}'.");
        }
    }

    private void ValidateSegment(string segment)
    {
        if (segment == "#" || KeyRegex.IsMatch(segment))
            return;

        throw new Z0ParserException($"Invalid key segment '{segment}'.");
    }
}

public static class Z0ParserMiniExtensions
{
    public static bool KeyEquals(string? a, string? b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Length != b.Length)
            return false;

        for (int i = 0; i < a.Length; i++)
        {
            char ca = a[i];
            char cb = b[i];

            if (ca == cb)
                continue;

            ca = ca == '-' ? '_' : char.ToLowerInvariant(ca);
            cb = cb == '-' ? '_' : char.ToLowerInvariant(cb);
            if (ca != cb)
                return false;
        }

        return true;
    }

    public static int KeyHashCode(string obj)
    {
        int hash = 17;
        foreach (char c in obj)
        {
            char cc = c == '-' ? '_' : char.ToLowerInvariant(c);
            hash = hash * 31 + cc.GetHashCode();
        }

        return hash;
    }
}

public class Z0ParserException : Exception
{
    public int LineNumber { get; }

    public Z0ParserException(string message) : base(message)
    {
    }

    public Z0ParserException(int line, string message) : base($"Line {line}: {message}")
    {
        LineNumber = line;
    }
}

public class ZNode
{
    public static readonly ZNode NullNode = new NullZNode();

    private readonly ParsingNode node;

    public ZNode(ParsingNode node)
    {
        this.node = node;
    }

    public virtual bool Exists => true;
    public virtual ZNode Check => this;

    // Value Operations
    public string Optional(string defaultValue = "") => node.IsValue ? node.Value : defaultValue;
    public string Required() => Exists ? node.Value : throw new Z0ParserException("Attempted to access a missing value");


    // Array Operations
    public ZNode this[int index] => node.IsArray && node.TryGetChild(index.ToString(), out ParsingNode? child) ? new ZNode(child!) : NullNode;
    public int Count => node.ChildCount;
    public IEnumerator<ZNode> GetEnumerator() => node.IsArray ? node.GetChildren().Values.Select(child => new ZNode(child)).GetEnumerator() : Enumerable.Empty<ZNode>().GetEnumerator();

    // Dictionary Operations
    public ZNode this[string key] => node.TryGetChild(key, out ParsingNode? child) ? new ZNode(child!) : NullNode;
    public bool ContainsKey(string key) => node.TryGetChild(key, out _);
    public IEnumerable<string> Keys() => node.GetChildren().Keys;

    public static implicit operator bool(ZNode node) => node.Exists;
    public static implicit operator string(ZNode node) => node.Optional();

    private class NullZNode : ZNode
    {
        public NullZNode() : base(ParsingNode.NewRootNode())
        {
        }

        public override bool Exists => false;
        public override ZNode Check => throw new Z0ParserException("Attempted to access a missing node");
    }
}

public static class Z0
{
    public static ParsingNode Parse(IEnumerable<string> lines)
    {
        Parser parser = new(lines);
        return parser.Parse();
    }

    public static ParsingNode Parse(string text)
    {
        string[] lines = text.Split(["\r\n", "\n"], StringSplitOptions.None);
        return Parse(lines);
    }

    public static ZNode AsZNode(this ParsingNode node) => new(node);

    public static object AsObject(this ParsingNode node) => Normalize(node);

    public static object Normalize(ParsingNode node)
    {
        if (node.IsValue)
            return node.Value;

        IReadOnlyDictionary<string, ParsingNode> items = node.GetChildren();

        if (node.IsArray)
        {
            object[] array = new object[items.Count];
            foreach ((string key, ParsingNode value) in items)
                array[int.Parse(key)] = Normalize(value);
            return array;
        }
        else
        {
            Dictionary<string, object> dict = new Dictionary<string, object>(items.Count);
            foreach (KeyValuePair<string, ParsingNode> kvp in items)
                dict[kvp.Key] = Normalize(kvp.Value);
            return dict;
        }
    }
}