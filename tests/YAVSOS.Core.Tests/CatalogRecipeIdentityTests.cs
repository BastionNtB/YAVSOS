using System;
using System.Linq;
using Xunit;
using YAVSOS.Core.Catalog;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

public sealed class CatalogRecipeIdentityTests
{
    [Fact]
    public void RecipeIdentity_SameUnityNamePreservesDifferentDefinitionsAndReferences()
    {
        var identities=new CatalogRecipeIdentity();
        var rows=identities.Add(Recipe("shared","A"),new[]{Requirement(recipe:"shared")})
            .Concat(identities.Add(Recipe("shared","B"),new[]{Requirement(recipe:"shared",input:"Stone")}));
        var catalog=CatalogSnapshot.Build(new[]{Item(),Item("Stone"),Item("A"),Item("B")}.Concat(rows));
        Assert.Equal(2,catalog.Recipes.Count);
        foreach(var recipe in catalog.Recipes.Values)
        {
            Assert.Equal("shared",recipe.Value("unityName"));
            Assert.Equal(recipe.Value("output")=="A"?"Wood":"Stone",Assert.Single(catalog.Requirements[recipe.Identity]).Value("input"));
        }
    }
    [Fact]
    public void RecipeIdentity_EquivalentRegistrationsRemainDistinctAndReorderingIsStable()
    {
        CatalogSnapshot Build(bool reverse)
        {
            var identities=new CatalogRecipeIdentity();var definitions=new[]{Recipe("same","A"),Recipe("same","A"),Recipe("same","B")};
            if(reverse)Array.Reverse(definitions);
            return CatalogSnapshot.Build(new[]{Item(),Item("A"),Item("B")}.Concat(definitions.SelectMany(r=>identities.Add(r,new[]{Requirement(recipe:"same")}))));
        }
        var catalog=Build(false);Assert.Equal(3,catalog.Recipes.Count);Assert.Equal(3,catalog.Requirements.Count);
        Assert.Equal(catalog.Fingerprint,Build(true).Fingerprint);
    }
    [Fact]
    public void RecipeIdentity_MaximumNameAndRequirementSlotOrderRemainBounded()
    {
        var name=new string('r',2048);var identities=new CatalogRecipeIdentity();
        var requirements=new[]{Requirement("0",name),Requirement("1",name,"Stone")};
        var first=identities.Add(Recipe(name,"A"),requirements);
        var second=new CatalogRecipeIdentity().Add(Recipe(name,"A"),requirements.Reverse().ToArray());
        Assert.NotEqual(first[0].Identity,second[0].Identity);
        Assert.Equal(name,first[0].Value("unityName"));
        Assert.All(first.Concat(second),f=>Assert.InRange(f.Identity.Length,1,2048));
        Assert.Equal(first[0].Identity,first[1].Value("recipe"));
    }

    [Fact]
    public void RecipeIdentity_RequirementChangesAffectDefinitionIdentityIndependentlyOfRegistrationOrder()
    {
        CatalogSnapshot Build(bool reverse)
        {
            var identities = new CatalogRecipeIdentity(); var inputs = new[] { "Wood", "Stone" };
            if (reverse) Array.Reverse(inputs);
            return CatalogSnapshot.Build(new[] { Item(), Item("Stone"), Item("A") }.Concat(inputs.SelectMany(input =>
                identities.Add(Recipe("same", "A"), new[] { Requirement(recipe: "same", input: input) }))));
        }
        var forward = Build(false); var reversed = Build(true);
        Assert.Equal(2, forward.Recipes.Count); Assert.Equal(forward.Fingerprint, reversed.Fingerprint);
        Assert.Equal(new[] { "Stone", "Wood" }, forward.Requirements.Values.Select(r => Assert.Single(r).Value("input")).OrderBy(x => x));
    }
}
