using System;

namespace Unai.ExtendedBinaryWaterfall;

public class CliParameterAttribute : Attribute
{
	public string LongParameterName { get; set; } = null;
	public Nullable<char> ShortParameterName { get; set; } = null;
	public string Name { get; set; } = null;
	public string Description { get; set; } = null;

	public CliParameterAttribute(string name, string longParamName, char shortParamName, string desc = null)
	{
		Name = name;
		LongParameterName = longParamName;
		ShortParameterName = shortParamName;
		Description = desc;
	}

	public CliParameterAttribute(string name, string longParamName, string desc = null)
	{
		Name = name;
		LongParameterName = longParamName;
		Description = desc;
	}

	public CliParameterAttribute() {}
}
