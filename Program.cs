using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Reflection;

using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;

using CWTools.Common;
using CWTools.Games;
using CWTools.Validation;
using static CWTools.Games.Files;

using StringDict = System.Collections.Generic.Dictionary<string, string>;

using YamlDeserializer = YamlDotNet.Serialization.Deserializer;
using YamlSerializerBuilder = YamlDotNet.Serialization.SerializerBuilder;
using IYamlSerializer = YamlDotNet.Serialization.ISerializer;

using CWNode = CWTools.Process.Node;
using CWValue = CWTools.Parser.Types.Value;
using SECData = CWTools.Games.ScriptedEffectComputedData;

Stopwatch timer = Stopwatch.StartNew();
// Add support for codepage 1252, used by CWTools
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
YamlDeserializer deserializer = new();


#region Read parameters

Options options = deserializer.Deserialize<Options>(File.ReadAllText("./config.yml"));

string gamePath = Path.GetFullPath(options.GameDir);
DirectoryInfo outDir = new(options.OutDir);

try {
	outDir.Delete(true);
} catch (DirectoryNotFoundException) {
}

outDir.Create();

if (options.ModPaths == null) {
	options.ModPaths = [];
}

bool hasMods = options.ModPaths.Count > 0;
List<WorkspaceDirectoryInput> resourceDirs = [
	WorkspaceDirectoryInput.NewWD(new WorkspaceDirectory(
		gamePath, "stellaris"
	)),
	.. options.ModPaths.Select(i => {
		string path = Path.GetFullPath(i);

		if (i[^1] is '/' or '\\') {
			return WorkspaceDirectoryInput.NewWD(new WorkspaceDirectory(
				path, Path.GetDirectoryName(path)
			));
		} else if (Path.GetExtension(path) == ".zip") {
			string root = path.Replace('\\', '/');
			return WorkspaceDirectoryInput.NewZD(new(
				path,
				Path.GetFileName(path),
				ZipFile.OpenRead(path).Entries
					.Select(i => Tuple.Create($"{root}/{i.FullName}", i.Open().ReadToString()))
					.ToFSharpList()
			));
		}

		throw new InvalidDataException($"Ill-formed mod path: {i}");
	})
];

#endregion

#region Load Data

CWTools.Games.Stellaris.STLGame stellaris = new(new GameSetupSettings<STLLookup>(
	resourceDirs.ToFSharpList(),
	EmbeddedSetupSettings.NewFromConfig(
		FSharpList<Tuple<string, string>>.Empty,
		FSharpList<Tuple<Resource, Entity>>.Empty
	),
	new ValidationSettings(LangHelpers.allSTLLangs, false, true),
	FSharpOption<RulesSettings>.Some(new(
		Directory
			.EnumerateFiles(options.ConfigDir, "*.???", SearchOption.AllDirectories)
			.Where(i => i.EndsWith(".cwt") || i.EndsWith(".log"))
			.Select(i => Tuple.Create(i, File.ReadAllText(i)))
			.ToFSharpList(),
		false,
		false,
		false
	)),
	null,
	null,
	null,
	null
));

var game = (GameObject<SECData, STLLookup>) typeof(CWTools.Games.Stellaris.STLGame)
	.GetField("game", BindingFlags.Instance | BindingFlags.NonPublic)!
	.GetValue(stellaris)!;

EntitySet<SECData> entities = new(game.Resources.AllEntities.Invoke(null));

Console.WriteLine("Loaded game data");

#endregion

#region Analyze technologies

CWComparer cwComparer = new(resourceDirs);

Dictionary<string, CWValue> variables = entities
	.AllOfType(STLConstants.EntityType.ScriptedVariables)
	.Select(i => i.Item1)
	.PipeIf(hasMods, x => x.ToSortedList(cwComparer))
	.SelectMany(i => i.Leaves)
	.ToDictionaryOverwriting(i => i.Key, i => i.Value);

Dictionary<string, Tech> techs = entities
	.AllOfType(STLConstants.EntityType.Technology)
	.Select(i => i.Item1)
	.PipeIf(hasMods, x => x.ToSortedList(cwComparer))
	.SelectMany(i => i.Children)
	.ToDictionaryOverwriting(
		i => i.Key,
		i => Tech.Parse(i, cwComparer, variables)
	);

foreach ((string id, Tech tech) in techs) {
	foreach (TechRequirement requirement in tech.Requires) {
		foreach (string req in requirement.Requirements()) {
			techs[req].Unlocks.Add(id);
		}
	}
}

Console.WriteLine($"Analyzed {techs.Count} technologies");

#endregion

#region Load localizations

Dictionary<Lang, Dictionary<string, Lazy<string>>> locs = game.LocalisationManager.LocalisationEntries()
	.ToDictionary(
		x => x.Item1,
		x => x.Item2
			.AsEnumerable()
			.PipeIf(hasMods, x => x.ToSortedList((x, y) => cwComparer.Compare(x.Item2, y.Item2)))
			.ToDictionaryOverwriting(
				i => i.Item1,
				i => new Lazy<string>(() => {
					string value = i.Item2.desc;
					try {
						return deserializer.Deserialize<string>(value);
					} catch (YamlDotNet.Core.YamlException) {
						// PDX doesn't use "standard" YAML.
						// For example quotes within quotes with no escapes.
						// Hence just do naive process as fallback here.
						return value.StartsWith('"') && value.EndsWith('"')
							? value[1..^1]
							: value;
					}
				})
			)
	);

Console.WriteLine("Loaded localization");

#endregion

#region Emit localizations

HashSet<string> allTechIds = [.. techs.SelectMany(i => i.Value.Swaps.Keys.Prepend(i.Key))];
Dictionary<string, StringDict> generatedLocs = LangHelpers.allSTLLangs.ToDictionary(
	i => i.LangId(),
	i => new StringDict()
);

foreach ((string id, Tech tech) in techs) {
	StringBuilder sb = new();

	if (tech.Dangerous) {
		sb.Append(LocConsts.Sep).Append(LocConsts.Dangerous);
	}

	if (tech.Rare) {
		sb.Append(LocConsts.Sep).Append(LocConsts.Rare);
	}

	if (tech.Levels != 1) {
		sb.Append(LocConsts.Sep).Append(LocConsts.Repeatable);
		if (tech.Levels != -1) {
			sb.Append(LocConsts.Times)
				.Append(tech.Levels);
		}
	}

	sb.Append($"{LocConsts.RParen}§!");

	void BuildRelatedTechString(string techId, int indent) {
		Tech tech = techs[techId];

		sb.Append("\n$");
		for (int i = 0; i < indent; i++) {
			sb.Append('t');
		}

		sb.Append($"${LocConsts.Bullet}");
		if (techs[id].Vanilla && !tech.Vanilla) {
			sb.Append(LocConsts.Mod);
		}

		sb.Append($"£{tech.Area.ToString().ToLowerInvariant()}£ ['technology:{techId}']");
	}

	if (tech.Requires.Count > 0) {
		sb.Append($"\n\n{LocConsts.Requires}");
		foreach (TechRequirement require in tech.Requires) {
			if (require is TechRequirementSingle single) {
				BuildRelatedTechString(single.Id, 1);
			} else if (require is TechRequirementAlternatives alternatives) {
				sb.Append($"\n$t${LocConsts.Bullet}{LocConsts.OneOf}");
				foreach (string alternative in alternatives.Alternatives) {
					BuildRelatedTechString(alternative, 2);
				}
			} else {
				throw new NotSupportedException();
			}
		}
	}

	if (tech.Unlocks.Count > 0) {
		sb.Append($"\n\n{LocConsts.Unlocks}");
		foreach (string unlock in tech.Unlocks) {
			BuildRelatedTechString(unlock, 1);
		}
	}

	string content = sb.ToString();
	string key = $"{id}_desc";

	foreach ((string swapId, TechSwap swapTech) in tech.Swaps.Prepend(new(id, tech.AsSwap()))) {
		string value = $"\n\n£{swapTech.Area.ToString().ToLowerInvariant()}£"
				+ $" §Y${swapTech.Area.ToString().ToUpperInvariant()}$ "
				+ $"T{tech.Tier}{LocConsts.LParen}"
				+ swapTech.Categories.Select(i => $"${i}$").ToArray().Join(LocConsts.Sep)
				+ content;

		foreach ((Lang lang, Dictionary<string, Lazy<string>> loc) in locs) {
			StringDict dict = generatedLocs[lang.LangId()];
			foreach (string suffix in LocConsts.suffixes) {
				string swapKey = $"{swapId}_desc{suffix}";

				if (suffix.Length > 0 && !loc.ContainsKey(swapKey)) {
					continue;
				}

				if (!loc.TryGetValue(swapKey, out Lazy<string>? desc)) {
					desc = loc[key];
				}

				string descVal = desc.Value;
				Match refMatch = RegexLocReference().Match(desc.Value);
				if (refMatch.Success) {
					if (
						allTechIds.Contains(refMatch.Groups["id"].Value)
						&& loc.TryGetValue(refMatch.Groups["key"].Value, out Lazy<string>? refDesc)
					) {
						descVal = refDesc.Value;
					}
				}

				string finalValue = descVal + value;
				if (!dict.TryGetValue(key, out string? origValue) || origValue != finalValue) {
					dict[swapKey] = finalValue;
				}
			}
		}
	}
}

Console.WriteLine($"Generated {generatedLocs.Sum(i => i.Value.Count)} localization entries");

IYamlSerializer serializer = new YamlSerializerBuilder()
	.WithDefaultScalarStyle(YamlDotNet.Core.ScalarStyle.DoubleQuoted)
	.WithNewLine("\n")
	.Build();
Encoding utf8bom = new UTF8Encoding(true);
foreach ((string lang, StringDict loc) in generatedLocs) {
	// We use partly StringBuilder and partly YamlSerializer here.
	// Since we want:
	//   1. Never quote keys
	//   2. Always quote values
	StringBuilder sb = new($"l_{lang}:\n");

	foreach ((string key, string value) in loc) {
		sb.Append(' ')
			.Append(key)
			.Append(": ")
			.Append(serializer.Serialize(value));
	}

	File.WriteAllText(
		$"{outDir.FullName}/techrel_l_{lang}.yml",
		sb.ToString(),
		utf8bom
	);
}

Console.WriteLine($"Wrote {generatedLocs.Count} language files");

#endregion

Console.WriteLine($"Finished in {timer.Elapsed}");


#region Data models

public sealed class Options {
	private string? gameDir;

	public string GameDir {
		get => gameDir ?? throw new InvalidDataException("Game dir not specified");
		set => gameDir = value;
	}

	public string ConfigDir { get; set; } = "./cwtools-stellaris-config/config/";

	public string OutDir { get; set; } = "./out";

	public List<string> ModPaths { get; set; } = [];
}

public class Tech(
	bool vanilla,
	Area area,
	int tier,
	List<string> categories,
	int levels,
	bool dangerous,
	bool rare,
	List<TechRequirement> requires,
	Dictionary<string, TechSwap> swaps
) {
	public bool Vanilla { get; private init; } = vanilla;
	public Area Area { get; private init; } = area;
	public int Tier { get; private init; } = tier;
	public List<string> Categories { get; private init; } = categories;

	public int Levels { get; private init; } = levels;
	public bool Dangerous { get; private init; } = dangerous;
	public bool Rare { get; private init; } = rare;

	public List<TechRequirement> Requires { get; private init; } = requires;
	public List<string> Unlocks { get; private init; } = [];
	public Dictionary<string, TechSwap> Swaps { get; private init; } = swaps;

	public TechSwap AsSwap() => new(Vanilla, Area, Categories);

	public static Tech Parse(CWNode node, CWComparer cwComparer, Dictionary<string, CWValue> variables) {
		bool vanilla = cwComparer.IsVanilla(node.Position);
		Area area = Enum.Parse<Area>(node.Tag("area").Value.ToRawString(), true);

		string tierStr = node.Tag("tier").Value.ToRawString();
		if (!int.TryParse(tierStr, out int tier)) {
			if (!tierStr.StartsWith('@')) {
				throw new NotSupportedException($"Unexpected tier value: {tierStr}");
			}

			tier = ((CWValue.Int) variables[tierStr]).Item;
		}

		List<string> categories = [.. node.Child("category").Value.LeafValues.Select(i => i.ValueText)];

		int levels = node.Tag("levels")
			.Map(x => {
				string str = x.ToRawString();
				if (!int.TryParse(str, out int levels)) {
					if (!str.StartsWith('@')) {
						throw new NotSupportedException($"Unexpected levels value: {str}");
					}

					levels = ((CWValue.Int) variables[str]).Item;
				}

				return levels;
			})
			.UnwrapOr(1);

		bool dangerous = node.Tag("is_dangerous")
			.Map(x => x.ToRawString().Equals("yes", StringComparison.OrdinalIgnoreCase))
			.UnwrapOr(false);
		bool rare = node.Tag("is_rare")
			.Map(x => x.ToRawString().Equals("yes", StringComparison.OrdinalIgnoreCase))
			.UnwrapOr(false);

		List<TechRequirement> requires = node.Child("prerequisites")
			.Map(x => {
				var list = x.LeafValues
					.Select(x => new TechRequirementSingle(x.Value.ToRawString()) as TechRequirement)
					.ToList();

				foreach (CWNode clause in x.Children) {
					if (!string.Equals(clause.Key, "OR", StringComparison.OrdinalIgnoreCase)) {
						throw new NotSupportedException();
					}

					list.Add(new TechRequirementAlternatives(
						clause.LeafValues.Select(i => i.Value.ToRawString())
					));
				}

				return list;
			})
			.UnwrapOr([]);

		Tech tech = new(vanilla, area, tier, categories, levels, dangerous, rare, requires, []);
		foreach (CWNode swapNode in node.Childs("technology_swap")) {
			tech.Swaps[swapNode.Tag("name").Value.ToRawString()]
				= TechSwap.Parse(swapNode, cwComparer, tech);
		}

		return tech;
	}
}

public class TechSwap(
	bool vanilla,
	Area area,
	List<string> categories
) {
	public bool Vanilla { get; private init; } = vanilla;
	public Area Area { get; private init; } = area;
	public List<string> Categories { get; private init; } = categories;

	public static TechSwap Parse(CWNode node, CWComparer cwComparer, Tech parent) => new(
		cwComparer.IsVanilla(node.Position),
		node.Tag("area").Map(x => Enum.Parse<Area>(x.ToRawString(), true)).UnwrapOr(parent.Area),
		node.Child("category").Map(x => x.LeafValues.Select(i => i.ValueText).ToList()).UnwrapOr(parent.Categories)
	);
}

public enum Area {
	Physics,
	Society,
	Engineering,
}

public abstract class TechRequirement {
	public abstract IEnumerable<string> Requirements();
}

public class TechRequirementSingle(string id) : TechRequirement {
	public string Id { get; private init; } = id;

	public override IEnumerable<string> Requirements() => [Id];
}

public class TechRequirementAlternatives(IEnumerable<string> alternatives) : TechRequirement {
	public string[] Alternatives { get; private init; } = [.. alternatives];

	public override IEnumerable<string> Requirements() => Alternatives;
}

#endregion

#region Utilities

partial class Program {
	[GeneratedRegex(
		@"^\$(?<key>(?<id>.+)_desc(?:|_(?:machine_intelligence|hive_mind|corporate)))\$$",
		RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant
	)]
	private static partial Regex RegexLocReference();
}

public class CWComparer(List<WorkspaceDirectoryInput> inputs) : IComparer<CWNode>, IComparer<CWTools.Localisation.Entry>, IComparer<CWTools.Utilities.Position.range> {
	public string GamePath { get; private init; } = ((WorkspaceDirectoryInput.WD) inputs[0]).Item.path;

	public string[] ModPaths { get; private init; } = [.. inputs.Skip(1).Select(
		x => x switch {
			WorkspaceDirectoryInput.WD wd => wd.Item.path,
			WorkspaceDirectoryInput.ZD zd => zd.Item.path,
			_ => throw new NotImplementedException(),
		}
	)];

	public bool IsVanilla(CWTools.Utilities.Position.range range) =>
		range.FileName.StartsWith(GamePath);

	public int ModIndex(CWTools.Utilities.Position.range range) =>
		ModPaths.Index().First((i) => range.FileName.StartsWith(i.Item)).Index;

	public int Compare(CWNode? x, CWNode? y) {
		if (x == null) {
			return y == null ? 0 : -1;
		} else if (y == null) {
			return 1;
		}

		return Compare(x.Position, y.Position);
	}

	public int Compare(CWTools.Localisation.Entry x, CWTools.Localisation.Entry y) =>
		Compare(x.position, y.position);

	public int Compare(CWTools.Utilities.Position.range x, CWTools.Utilities.Position.range y) {
		bool isVanillaX = IsVanilla(x);
		bool isVanillaY = IsVanilla(y);

		if (isVanillaX) {
			return isVanillaY
				? string.Compare(
					x.FileName,
					y.FileName,
					StringComparison.OrdinalIgnoreCase
				)
				: -1;
		} else if (isVanillaY) {
			return 1;
		}

		int modIndexX = ModIndex(x);
		int modIndexY = ModIndex(y);
		return string.Compare(
				x.FileName[ModPaths[modIndexX].Length..],
				y.FileName[ModPaths[modIndexY].Length..],
				StringComparison.OrdinalIgnoreCase
			) switch {
				0 => modIndexX - modIndexY,
				int i => i
			};
	}
}

public static class Extensions {
	public static T PipeIf<T>(this T self, bool doPipe, Func<T, T> mapper) =>
		doPipe ? mapper(self) : self;

	public static string ReadToString(this Stream self) {
		using Stream stream = self;
		using StreamReader sr = new(stream, true);
		return sr.ReadToEnd();
	}

	public static FSharpList<T> ToFSharpList<T>(this IEnumerable<T> self) =>
		ListModule.OfSeq(self);

	public static Dictionary<TKey, TValue> ToDictionaryOverwriting<T, TKey, TValue>(this IEnumerable<T> self, Func<T, TKey> keySelector, Func<T, TValue> valueSelector) where TKey : notnull {
		Dictionary<TKey, TValue> dict = [];

		IEnumerator<T> enumerator = self.GetEnumerator();
		while (enumerator.MoveNext()) {
			dict[keySelector(enumerator.Current)] = valueSelector(enumerator.Current);
		}

		return dict;
	}

	public static List<T> ToSortedList<T>(this IEnumerable<T> self, IComparer<T> comparer) {
		List<T> list = [.. self];
		list.Sort(comparer);
		return list;
	}

	public static List<T> ToSortedList<T>(this IEnumerable<T> self, Comparison<T> comparison) {
		List<T> list = [.. self];
		list.Sort(comparison);
		return list;
	}

	public static string Join(this string[] self, char separator) {
		StringBuilder sb = new();
		sb.AppendJoin(separator, self);
		return sb.ToString();
	}

	public static FSharpOption<TTo> Map<T, TTo>(this FSharpOption<T> self, Func<T, TTo> mapper) =>
		OptionModule.IsSome(self)
			? FSharpOption<TTo>.Some(mapper.Invoke(self.Value))
			: FSharpOption<TTo>.None;

	public static T UnwrapOr<T>(this FSharpOption<T> self, T fallback) =>
		OptionModule.IsSome(self)
			? self.Value
			: fallback;

	public static string LangId(this Lang self) =>
		self is Lang.STL lang
			? lang.Item switch {
				STLLang.English => "english",
				STLLang.French => "french",
				STLLang.German => "german",
				STLLang.Spanish => "spanish",
				STLLang.Russian => "russian",
				STLLang.Polish => "polish",
				STLLang.Braz_Por => "braz_por",
				STLLang.Chinese => "simp_chinese",
				STLLang.Japanese => "japanese",
				STLLang.Korean => "korean",
				_ => throw new NotSupportedException()
			}
			: throw new NotSupportedException();
}

public static class LocConsts {
	public const string Repeatable = "$techrel_repeatable$";
	public const string Requires = "$techrel_requires$";
	public const string Unlocks = "$techrel_unlocks$";
	public const string OneOf = "$techrel_one_of$";

	public const char Times = '×';
	public const string Bullet = "$BULLET_POINT$";
	public const string Mod = "[Mod] ";
	public const string LParen = " (";
	public const char RParen = ')';
	public const char Sep = ' ';
	public const string Dangerous = "$TECH_IS_DANGEROUS$";
	public const string Rare = "$TECH_IS_RARE$";

	public static readonly IEnumerable<string> suffixes = [
		"",
		"_machine_intelligence",
		"_hive_mind",
		"_corporate"
	];
}

#endregion
