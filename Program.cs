using System.Linq.Expressions;
using Newtonsoft.Json;
using System.Reflection;
using System.Globalization;
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
						.Skip(10)
						.Limit(100)
						.ToString();
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
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
    }

    public SqlQueryBuilder<T> Select(
    Expression<Func<T, object>> columns,
    params (Expression<Func<T, object>> column, string function, string alias)[] aggregates)
{
    // Handle anonymous type or single column selection
    if (columns.Body is NewExpression newExpression)
    {
        // Anonymous type projection
        foreach (var argument in newExpression.Arguments)
        {
            if (argument is MemberExpression memberExpression)
            {
                _selectColumns.Add(GetColumnName(Expression.Lambda<Func<T, object>>(
                    memberExpression, columns.Parameters)));
            }
        }
    }
    else
    {
        // Single column selection
        _selectColumns.Add(GetColumnName(columns));
    }

    // Handle aggregate functions
    foreach (var (column, function, alias) in aggregates)
    {
        var columnName = GetColumnName(column);
        _selectColumns.Add($"{function}({columnName}) AS {alias}");
    }

    return this;
}

    public SqlQueryBuilder<T> Where(Expression<Func<T, bool>> predicate)
    {
        var condition = ExpressionToSql(predicate.Body);
        _whereConditions.Add(condition);
        return this;
    }

    public SqlQueryBuilder<T> GroupBy(params Expression<Func<T, object>>[] columns)
    {
        foreach (var column in columns)
        {
            var columnNames = GetColumnNamesFromExpression(column);
            _groupByColumns.AddRange(columnNames);
        }
        return this;
    }

    public SqlQueryBuilder<T> OrderBy(params Expression<Func<T, object>>[] columns)
    {
        foreach (var column in columns)
        {
            var columnNames = GetColumnNamesFromExpression(column);
            _orderByColumns.AddRange(columnNames.Select(c => $"{c} ASC"));
        }
        return this;
    }

    public SqlQueryBuilder<T> OrderByDescending(params Expression<Func<T, object>>[] columns)
    {
        foreach (var column in columns)
        {
            var columnNames = GetColumnNamesFromExpression(column);
            _orderByColumns.AddRange(columnNames.Select(c => $"{c} DESC"));
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
        _limit = limit;
        return this;
    }

    public SqlQueryBuilder<T> Skip(int offset)
    {
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
        var query = new StringBuilder();

        // SELECT clause
        query.Append("SELECT ");
        if (_distinct) query.Append("DISTINCT ");
        
        if (_selectColumns.Count == 0)
        {
            query.Append("*");
        }
        else
        {
            query.Append(string.Join(", ", _selectColumns));
        }

        // FROM clause
        query.Append($" FROM \"{_tableName}\"");

        // WHERE clause
        if (_whereConditions.Count > 0)
        {
            query.Append(" WHERE ");
            query.Append(string.Join(" AND ", _whereConditions));
        }

        // GROUP BY clause
        if (_groupByColumns.Count > 0)
        {
            query.Append(" GROUP BY ");
            query.Append(string.Join(", ", _groupByColumns));
        }

        // ORDER BY clause
        if (_orderByColumns.Count > 0)
        {
            query.Append(" ORDER BY ");
            query.Append(string.Join(", ", _orderByColumns));
        }

        // LIMIT clause
        if (_limit.HasValue)
        {
            query.Append($" LIMIT {_limit.Value}");
        }

        // OFFSET clause
        if (_offset.HasValue)
        {
            query.Append($" OFFSET {_offset.Value}");
        }

        // FOR UPDATE clause
        if (_forUpdate)
        {
            query.Append(" FOR UPDATE");
        }

        return query.ToString();
    }

    private string GetColumnName(Expression<Func<T, object>> expression)
{
    MemberExpression memberExpression = null;

    if (expression.Body is MemberExpression memExpr)
    {
        memberExpression = memExpr;
    }
    else if (expression.Body is UnaryExpression unaryExpr && 
            unaryExpr.Operand is MemberExpression unaryMemExpr)
    {
        memberExpression = unaryMemExpr;
    }

    if (memberExpression == null)
        throw new ArgumentException("Invalid member expression");

    // Check for Column attribute
    var columnAttr = memberExpression.Member.GetCustomAttribute<ColumnAttribute>();
    if (columnAttr != null)
    {
        return $"\"{columnAttr.Name}\"";
    }
    
    return $"\"{memberExpression.Member.Name}\"";
}

    private List<string> GetColumnNamesFromExpression(Expression<Func<T, object>> expression)
    {
        // Handle anonymous types and multiple columns
        if (expression.Body is NewExpression newExpression)
        {
            var columnNames = new List<string>();
            foreach (var argument in newExpression.Arguments)
            {
                if (argument is MemberExpression memberExpression)
                {
                    columnNames.Add(GetColumnName(Expression.Lambda<Func<T, object>>(memberExpression)));
                }
            }
            return columnNames;
        }

        // Handle single column
        return new List<string> { GetColumnName(expression) };
    }

    private string ExpressionToSql(Expression expression)
    {
        switch (expression.NodeType)
        {
            case ExpressionType.Equal:
                var equalExpr = (BinaryExpression)expression;
                return $"{ExpressionToSql(equalExpr.Left)} = {ExpressionToSql(equalExpr.Right)}";
                
            case ExpressionType.NotEqual:
                var notEqualExpr = (BinaryExpression)expression;
                return $"{ExpressionToSql(notEqualExpr.Left)} <> {ExpressionToSql(notEqualExpr.Right)}";
                
            case ExpressionType.GreaterThan:
                var gtExpr = (BinaryExpression)expression;
                return $"{ExpressionToSql(gtExpr.Left)} > {ExpressionToSql(gtExpr.Right)}";
                
            case ExpressionType.GreaterThanOrEqual:
                var gteExpr = (BinaryExpression)expression;
                return $"{ExpressionToSql(gteExpr.Left)} >= {ExpressionToSql(gteExpr.Right)}";
                
            case ExpressionType.LessThan:
                var ltExpr = (BinaryExpression)expression;
                return $"{ExpressionToSql(ltExpr.Left)} < {ExpressionToSql(ltExpr.Right)}";
                
            case ExpressionType.LessThanOrEqual:
                var lteExpr = (BinaryExpression)expression;
                return $"{ExpressionToSql(lteExpr.Left)} <= {ExpressionToSql(lteExpr.Right)}";
                
            case ExpressionType.AndAlso:
                var andExpr = (BinaryExpression)expression;
                return $"({ExpressionToSql(andExpr.Left)} AND {ExpressionToSql(andExpr.Right)})";
                
            case ExpressionType.OrElse:
                var orExpr = (BinaryExpression)expression;
                return $"({ExpressionToSql(orExpr.Left)} OR {ExpressionToSql(orExpr.Right)})";
                
            case ExpressionType.MemberAccess:
                var memberExpr = (MemberExpression)expression;
                return GetColumnName(Expression.Lambda<Func<T, object>>(memberExpr));
                
            case ExpressionType.Constant:
                var constExpr = (ConstantExpression)expression;
                if (constExpr.Value is string)
                    return $"'{constExpr.Value.ToString().Replace("'", "''")}'";
                if (constExpr.Value is bool)
                    return (bool)constExpr.Value ? "TRUE" : "FALSE";
                return constExpr.Value?.ToString() ?? "NULL";
                
            default:
                throw new NotSupportedException($"Expression type {expression.NodeType} is not supported");
        }
    }
}