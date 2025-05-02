using System.Linq.Expressions;
using Newtonsoft.Json;
using System.Reflection;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text;

public class SqlBuilderApp
{
	public static void Main(string[] args)
	{
		var statement = new SqlQueryBuilder<DatabricksDto>("DatabricksTable")
						.Select(x => new { x.Id, x.Name, x.CreatedAt }, (x => x.BreakType, "COUNT", "TotalRecords"))
						.Where(x => x.Id == "1")
						.GroupBy(x => x.BreakType)
						.OrderBy(x => x.CreatedAt)
						.Offset(10)
						.Limit(100)
						.Build();
		Console.WriteLine(statement);
	}
}

public class DatabricksDto
{
	public string? Id { get; set; }

	public string? Name { get; set; }

	[JsonProperty("break_type")]
	public string? BreakType { get; set; }

	[JsonProperty("created_at")]
	public DateTime CreatedAt { get; set; }
}

public class SqlQueryBuilder<T>
{
    private readonly string _tableName;
    private readonly List<string> _selectColumns = new List<string>();
    private readonly List<string> _whereConditions = new List<string>();
    private readonly List<string> _groupByColumns = new List<string>();
    private readonly List<string> _orderByColumns = new List<string>();
    private bool _distinct = false;
    private int? _limit;
    private int? _offset;
    private bool _forUpdate;

    public SqlQueryBuilder(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            throw new ArgumentException("Table name cannot be null or empty", nameof(tableName));
        
        _tableName = tableName;
    }

    public SqlQueryBuilder<T> Select(
        Expression<Func<T, object>> columns,
        params (Expression<Func<T, object>> column, string function, string alias)[] aggregates)
    {
        if (columns == null)
            throw new ArgumentNullException(nameof(columns));

        try
        {
            // Handle main projection (anonymous type or single column)
            if (columns.Body is NewExpression newExpr)
            {
                ProcessNewExpression(newExpr);
            }
            else
            {
                _selectColumns.Add(GetColumnName(columns));
            }

            // Handle aggregates
            foreach (var (column, function, alias) in aggregates)
            {
                if (column == null)
                    throw new ArgumentNullException(nameof(column));
                if (string.IsNullOrWhiteSpace(function))
                    throw new ArgumentException("Aggregate function cannot be null or empty", nameof(function));
                if (string.IsNullOrWhiteSpace(alias))
                    throw new ArgumentException("Alias cannot be null or empty", nameof(alias));

                var columnName = GetColumnName(column);
                _selectColumns.Add($"{function.ToUpper()}({columnName}) AS \"{alias}\"");
            }
        }
        catch (Exception ex)
        {
            throw new ArgumentException("Invalid SELECT expression", ex);
        }

        return this;
    }

    private void ProcessNewExpression(NewExpression newExpr)
    {
        foreach (var arg in newExpr.Arguments)
        {
            switch (arg)
            {
                case MemberExpression m:
                    _selectColumns.Add(GetColumnNameFromMember(m));
                    break;
                case UnaryExpression u when u.Operand is MemberExpression m:
                    _selectColumns.Add(GetColumnNameFromMember(m));
                    break;
                default:
                    throw new ArgumentException("Anonymous type projections can only contain member access expressions");
            }
        }
    }

    public SqlQueryBuilder<T> Where(Expression<Func<T, bool>> predicate)
    {
        if (predicate == null)
            throw new ArgumentNullException(nameof(predicate));

        try
        {
            var condition = ExpressionToSql(predicate.Body);
            _whereConditions.Add(condition);
        }
        catch (Exception ex)
        {
            throw new ArgumentException("Invalid WHERE expression", ex);
        }

        return this;
    }

    public SqlQueryBuilder<T> GroupBy(params Expression<Func<T, object>>[] columns)
    {
        if (columns == null || columns.Length == 0)
            throw new ArgumentException("At least one GROUP BY column must be specified");

        try
        {
            foreach (var column in columns)
            {
                var columnNames = GetColumnNamesFromExpression(column);
                _groupByColumns.AddRange(columnNames);
            }
        }
        catch (Exception ex)
        {
            throw new ArgumentException("Invalid GROUP BY expression", ex);
        }

        return this;
    }

    public SqlQueryBuilder<T> OrderBy(params Expression<Func<T, object>>[] columns)
    {
        if (columns == null || columns.Length == 0)
            throw new ArgumentException("At least one ORDER BY column must be specified");

        try
        {
            foreach (var column in columns)
            {
                var columnNames = GetColumnNamesFromExpression(column);
                _orderByColumns.AddRange(columnNames.Select(c => $"{c} ASC"));
            }
        }
        catch (Exception ex)
        {
            throw new ArgumentException("Invalid ORDER BY expression", ex);
        }

        return this;
    }

    public SqlQueryBuilder<T> OrderByDescending(params Expression<Func<T, object>>[] columns)
    {
        if (columns == null || columns.Length == 0)
            throw new ArgumentException("At least one ORDER BY column must be specified");

        try
        {
            foreach (var column in columns)
            {
                var columnNames = GetColumnNamesFromExpression(column);
                _orderByColumns.AddRange(columnNames.Select(c => $"{c} DESC"));
            }
        }
        catch (Exception ex)
        {
            throw new ArgumentException("Invalid ORDER BY expression", ex);
        }

        return this;
    }

    public SqlQueryBuilder<T> Distinct()
    {
        _distinct = true;
        return this;
    }

    public SqlQueryBuilder<T> Limit(int limit)
    {
        if (limit <= 0)
            throw new ArgumentException("LIMIT must be greater than 0", nameof(limit));
        
        _limit = limit;
        return this;
    }

    public SqlQueryBuilder<T> Offset(int offset)
    {
        if (offset < 0)
            throw new ArgumentException("OFFSET must be 0 or greater", nameof(offset));
        
        _offset = offset;
        return this;
    }

    public SqlQueryBuilder<T> ForUpdate()
    {
        _forUpdate = true;
        return this;
    }

    public string Build()
    {
        if (_selectColumns.Count == 0 && _groupByColumns.Count > 0)
            throw new InvalidOperationException("When using GROUP BY, you must specify columns in SELECT");

        var query = new StringBuilder();

        // SELECT clause
        query.Append("SELECT ");
        if (_distinct) query.Append("DISTINCT ");
        query.Append(_selectColumns.Count == 0 ? "*" : string.Join(", ", _selectColumns));

        // FROM clause
        query.Append($" FROM \"{_tableName}\"");

        // WHERE clause
        if (_whereConditions.Count > 0)
            query.Append(" WHERE ").Append(string.Join(" AND ", _whereConditions));

        // GROUP BY clause
        if (_groupByColumns.Count > 0)
            query.Append(" GROUP BY ").Append(string.Join(", ", _groupByColumns));

        // ORDER BY clause
        if (_orderByColumns.Count > 0)
            query.Append(" ORDER BY ").Append(string.Join(", ", _orderByColumns));

        // LIMIT/OFFSET
        if (_limit.HasValue) query.Append($" LIMIT {_limit.Value}");
        if (_offset.HasValue) query.Append($" OFFSET {_offset.Value}");

        // FOR UPDATE
        if (_forUpdate) query.Append(" FOR UPDATE");

        return query.ToString();
    }

    private string GetColumnName(Expression<Func<T, object>> expression)
    {
        var parameter = Expression.Parameter(typeof(T), "x");
        var visitor = new ParameterReplaceVisitor(expression.Parameters[0], parameter);
        var newBody = visitor.Visit(expression.Body);

        switch (newBody)
        {
            case MemberExpression m:
                return GetColumnNameFromMember(m);
            case UnaryExpression u when u.Operand is MemberExpression m:
                return GetColumnNameFromMember(m);
            default:
                throw new ArgumentException("Expression must be a member access");
        }
    }

    private List<string> GetColumnNamesFromExpression(Expression<Func<T, object>> expression)
    {
        var parameter = Expression.Parameter(typeof(T), "x");
        var visitor = new ParameterReplaceVisitor(expression.Parameters[0], parameter);
        var newBody = visitor.Visit(expression.Body);

        if (newBody is NewExpression newExpr)
        {
            var names = new List<string>();
            foreach (var arg in newExpr.Arguments)
            {
                switch (arg)
                {
                    case MemberExpression m:
                        names.Add(GetColumnNameFromMember(m));
                        break;
                    case UnaryExpression u when u.Operand is MemberExpression m:
                        names.Add(GetColumnNameFromMember(m));
                        break;
                    default:
                        throw new ArgumentException("Anonymous type projections can only contain member access expressions");
                }
            }
            return names;
        }

        return new List<string> { GetColumnName(expression) };
    }

    private string GetColumnNameFromMember(MemberExpression memberExpression)
    {
        var columnAttr = memberExpression.Member.GetCustomAttribute<ColumnAttribute>();
        return columnAttr != null ? $"\"{columnAttr.Name}\"" : $"\"{memberExpression.Member.Name}\"";
    }

    private string ExpressionToSql(Expression expression)
    {
        switch (expression)
        {
            case BinaryExpression binary:
                return HandleBinaryExpression(binary);
            case MemberExpression member:
                return HandleMemberExpression(member);
            case ConstantExpression constant:
                return HandleConstantExpression(constant);
            case UnaryExpression unary when unary.NodeType == ExpressionType.Convert:
                return ExpressionToSql(unary.Operand);
            default:
                throw new NotSupportedException($"Expression type {expression.GetType().Name} is not supported");
        }
    }

    private string HandleBinaryExpression(BinaryExpression binary)
    {
        var left = ExpressionToSql(binary.Left);
        var right = ExpressionToSql(binary.Right);

        return binary.NodeType switch
        {
            ExpressionType.Equal => $"{left} = {right}",
            ExpressionType.NotEqual => $"{left} <> {right}",
            ExpressionType.GreaterThan => $"{left} > {right}",
            ExpressionType.GreaterThanOrEqual => $"{left} >= {right}",
            ExpressionType.LessThan => $"{left} < {right}",
            ExpressionType.LessThanOrEqual => $"{left} <= {right}",
            ExpressionType.AndAlso => $"({left} AND {right})",
            ExpressionType.OrElse => $"({left} OR {right})",
            _ => throw new NotSupportedException($"Binary operator {binary.NodeType} is not supported")
        };
    }

    private string HandleMemberExpression(MemberExpression member)
    {
        return GetColumnNameFromMember(member);
    }

    private string HandleConstantExpression(ConstantExpression constant)
    {
        if (constant.Value == null) return "NULL";
        if (constant.Value is string str) return $"'{str.Replace("'", "''")}'";
        if (constant.Value is bool b) return b ? "TRUE" : "FALSE";
        if (constant.Value is DateTime dt) return $"'{dt:yyyy-MM-dd HH:mm:ss}'";
        return constant.Value.ToString();
    }

    private class ParameterReplaceVisitor : ExpressionVisitor
    {
        private readonly ParameterExpression _oldParam;
        private readonly ParameterExpression _newParam;

        public ParameterReplaceVisitor(ParameterExpression oldParam, ParameterExpression newParam)
        {
            _oldParam = oldParam;
            _newParam = newParam;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            return node == _oldParam ? _newParam : base.VisitParameter(node);
        }
    }
}