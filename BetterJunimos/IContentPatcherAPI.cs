using System;
using System.Collections.Generic;
using StardewModdingAPI;

namespace BetterJunimos;

public interface IContentPatcherAPI
{
	bool IsConditionsApiReady { get; }

	void RegisterToken(IManifest mod, string name, Func<IEnumerable<string>> getValue);

	void RegisterToken(IManifest mod, string name, object token);
}
