using System.Linq.Expressions;
using Newtonsoft.Json;
using System.Reflection;
using System.Globalization;
using System.ComponentModel.DataAnnotations.Schema;

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
	private readonly List<string> _selectColumns = new();
	private readonly List<string> _aggregateColumns = new();
	private string _whereClause = string.Empty;
	private readonly List<string> _groupByColumns = new();
	private readonly List<string> _orderByColumns = new();
	private string _havingClause = string.Empty;
	private readonly List<string> _joins = new();
	private readonly Dictionary<string, object> _insertValues = new();
	private readonly Dictionary<string, object> _updateValues = new();
	private string _deleteCondition = string.Empty;
	private int? _skip;
	private int? _limit;

	public SqlQueryBuilder(string tableName)
	{
		if (string.IsNullOrWhiteSpace(tableName))
			throw new ArgumentException("Table name cannot be null or empty", nameof(tableName));

		_tableName = tableName;
	}

	public SqlQueryBuilder<T> Select(Expression<Func<T, object>> selector, params (Expression<Func<T, object>> column, string function, string alias)[] aggregates)
	{
		if (selector == null) throw new ArgumentNullException(nameof(selector));

		_selectColumns.AddRange(ParseSelectedColumns(selector.Body));

		if (aggregates != null && aggregates.Length > 0)
		{
			_aggregateColumns.AddRange(aggregates
				.Where(a => a.column != null && !string.IsNullOrWhiteSpace(a.function) && !string.IsNullOrWhiteSpace(a.alias))
				.Select(a => $"{a.function}({ParseExpression(a.column.Body)}) AS {a.alias}"));
		}

		return this;
	}

	public SqlQueryBuilder<T> Where(Expression<Func<T, bool>> predicate)
	{
		if (predicate == null) throw new ArgumentNullException(nameof(predicate));

		_whereClause = $"WHERE {ParseExpression(predicate.Body)}";
		return this;
	}

	public SqlQueryBuilder<T> GroupBy(Expression<Func<T, object>> selector)
	{
		if (selector == null) throw new ArgumentNullException(nameof(selector));

		_groupByColumns.AddRange(ParseSelectedColumns(selector.Body));
		return this;
	}

	public SqlQueryBuilder<T> OrderBy(Expression<Func<T, object>> selector, bool descending = false)
	{
		if (selector == null) throw new ArgumentNullException(nameof(selector));

		var orderColumns = ParseSelectedColumns(selector.Body);
		_orderByColumns.AddRange(orderColumns.Select(col => descending ? $"{col} DESC" : col));
		return this;
	}

	public SqlQueryBuilder<T> Having(Expression<Func<T, bool>> condition)
	{
		if (condition == null) throw new ArgumentNullException(nameof(condition));

		_havingClause = $"HAVING {ParseExpression(condition.Body)}";
		return this;
	}

	public SqlQueryBuilder<T> Join<U>(string joinType, string joinTable, Expression<Func<T, object>> primaryKey, Expression<Func<U, object>> foreignKey)
	{
		if (string.IsNullOrWhiteSpace(joinType)) throw new ArgumentException("Join type cannot be null or empty", nameof(joinType));
		if (string.IsNullOrWhiteSpace(joinTable)) throw new ArgumentException("Join table cannot be null or empty", nameof(joinTable));
		if (primaryKey == null) throw new ArgumentNullException(nameof(primaryKey));
		if (foreignKey == null) throw new ArgumentNullException(nameof(foreignKey));

		_joins.Add($"{joinType.ToUpperInvariant()} JOIN {joinTable} ON {_tableName}.{ParseExpression(primaryKey.Body)} = {joinTable}.{ParseExpression(foreignKey.Body)}");
		return this;
	}

	public SqlQueryBuilder<T> Insert(Dictionary<string, object> values)
	{
		if (values == null) throw new ArgumentNullException(nameof(values));
		if (values.Count == 0) throw new ArgumentException("Insert values cannot be empty", nameof(values));

		_insertValues.Clear();
		foreach (var kvp in values.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key)))
		{
			_insertValues[kvp.Key] = kvp.Value;
		}
		return this;
	}

	public SqlQueryBuilder<T> Update(Dictionary<string, object> values)
	{
		if (values == null) throw new ArgumentNullException(nameof(values));
		if (values.Count == 0) throw new ArgumentException("Update values cannot be empty", nameof(values));

		_updateValues.Clear();
		foreach (var kvp in values.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key)))
		{
			_updateValues[kvp.Key] = kvp.Value;
		}
		return this;
	}

	public SqlQueryBuilder<T> Delete(Expression<Func<T, bool>> condition)
	{
		if (condition == null) throw new ArgumentNullException(nameof(condition));

		_deleteCondition = $"WHERE {ParseExpression(condition.Body)}";
		return this;
	}

	public SqlQueryBuilder<T> Skip(int count)
	{
		if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), "Skip count cannot be negative");
		_skip = count;
		return this;
	}

	public SqlQueryBuilder<T> Limit(int count)
	{
		if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count), "Limit must be positive");
		_limit = count;
		return this;
	}

	public override string ToString()
	{
		if (_insertValues.Count > 0)
		{
			var insertColumns = string.Join(", ", _insertValues.Keys);
			var values = string.Join(", ", _insertValues.Values.Select(v => FormatValue(v)));
			return $"INSERT INTO {_tableName} ({insertColumns}) VALUES ({values})";
		}

		if (_updateValues.Count > 0)
		{
			var updates = string.Join(", ", _updateValues.Select(kvp => $"{kvp.Key} = {FormatValue(kvp.Value)}"));
			return $"UPDATE {_tableName} SET {updates} {_whereClause}";
		}

		if (!string.IsNullOrEmpty(_deleteCondition))
		{
			return $"DELETE FROM {_tableName} {_deleteCondition}";
		}

		var columns = GetSelectColumns();
		var joins = _joins.Count > 0 ? " " + string.Join(" ", _joins) : string.Empty;
		var groupBy = _groupByColumns.Count > 0 ? $" GROUP BY {string.Join(", ", _groupByColumns)}" : string.Empty;
		var orderBy = _orderByColumns.Count > 0 ? $" ORDER BY {string.Join(", ", _orderByColumns)}" : string.Empty;
		var limit = _limit.HasValue ? $" LIMIT {_limit.Value}" : string.Empty;
		var offset = _skip.HasValue ? $" OFFSET {_skip.Value}" : string.Empty;

		return $"SELECT {columns} FROM {_tableName}{joins} {_whereClause}{groupBy} {_havingClause}{orderBy}{limit}{offset}"
			.Replace("  ", " ").Trim(); // Clean up any double spaces
	}

	private string GetSelectColumns()
	{
		if (_selectColumns.Count == 0 && _aggregateColumns.Count == 0)
			return "*";

		var columns = _selectColumns.Count > 0 ? string.Join(", ", _selectColumns) : string.Empty;

		if (_aggregateColumns.Count > 0)
		{
			columns = string.IsNullOrEmpty(columns)
				? string.Join(", ", _aggregateColumns)
				: $"{columns}, {string.Join(", ", _aggregateColumns)}";
		}

		return columns;
	}

	private List<string> ParseSelectedColumns(Expression expression)
	{
		if (expression == null)
			throw new ArgumentNullException(nameof(expression));

		return expression switch
		{
			NewExpression newExp => newExp.Members?.Select(m => GetColumnName(m.Name)).ToList() ?? new List<string>(),
			MemberExpression memberExp => new List<string> { GetColumnName(memberExp.Member.Name) },
			_ => new List<string>()
		};
	}

	private static string FormatValue(object? value)
	{
		if (value == null) return "NULL";

		return value switch
		{
			string s => $"'{s.Replace("'", "''")}'",
			bool b => b ? "1" : "0",
			DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
			DateTimeOffset dto => $"'{dto:yyyy-MM-dd HH:mm:ss}'",
			IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
			_ => $"'{value}'"
		};
	}

	private string ParseExpression(Expression expression)
	{
		if (expression == null)
			throw new ArgumentNullException(nameof(expression));

		return expression switch
		{
			BinaryExpression binaryExp =>
				$"({ParseExpression(binaryExp.Left)} {GetSqlOperator(binaryExp.NodeType)} {ParseExpression(binaryExp.Right)})",
			MemberExpression memberExp =>
				GetColumnName(memberExp.Member.Name),
			ConstantExpression constExp =>
				FormatValue(constExp.Value),
			MethodCallExpression methodCall =>
				ParseMethodCall(methodCall),
			_ => throw new NotSupportedException($"Expression of type {expression.NodeType} is not supported")
		};
	}

	private string ParseMethodCall(MethodCallExpression methodCall)
	{
		if (methodCall == null)
			throw new ArgumentNullException(nameof(methodCall));

		var methodName = methodCall.Method.Name;
		var arguments = methodCall.Arguments.Select(a => ParseExpression(a)).ToList();

		if (methodCall.Object == null)
			throw new ArgumentException("Method call object cannot be null");

		return methodName switch
		{
			"Contains" when arguments.Count == 1 =>
				$"{ParseExpression(methodCall.Object)} LIKE '%{arguments[0].Trim('\'')}%'",
			"StartsWith" when arguments.Count == 1 =>
				$"{ParseExpression(methodCall.Object)} LIKE '{arguments[0].Trim('\'')}%'",
			"EndsWith" when arguments.Count == 1 =>
				$"{ParseExpression(methodCall.Object)} LIKE '%{arguments[0].Trim('\'')}'",
			_ => throw new NotSupportedException($"Method {methodName} is not supported")
		};
	}

	private static string GetSqlOperator(ExpressionType type) => type switch
	{
		ExpressionType.Equal => "=",
		ExpressionType.NotEqual => "!=",
		ExpressionType.LessThan => "<",
		ExpressionType.GreaterThan => ">",
		ExpressionType.LessThanOrEqual => "<=",
		ExpressionType.GreaterThanOrEqual => ">=",
		ExpressionType.AndAlso => "AND",
		ExpressionType.OrElse => "OR",
		_ => throw new NotSupportedException($"Unsupported operator: {type}")
	};

	private string GetColumnName(string propertyName)
	{
		if (string.IsNullOrWhiteSpace(propertyName))
			throw new ArgumentException("Property name cannot be null or empty", nameof(propertyName));

		var property = typeof(T).GetProperty(propertyName);
		if (property == null)
			throw new ArgumentException($"Property {propertyName} not found on type {typeof(T).Name}");

		var columnName = property.GetCustomAttribute<ColumnAttribute>()?.Name;
		return columnName ?? propertyName;
	}
}

