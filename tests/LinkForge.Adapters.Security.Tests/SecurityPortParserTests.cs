using LinkForge.Adapters.Security;

namespace LinkForge.Adapters.Security.Tests;

public class SecurityPortParserTests
{
    [Fact]
    public void Comma_separated_ports_should_be_deduplicated_and_sorted()
    {
        SecurityPortParser.TryParse("443, 80,443;554", 32, out var ports, out var error)
            .ShouldBeTrue();

        error.ShouldBeNull();
        ports.ShouldBe([80, 443, 554]);
    }

    [Fact]
    public void Chinese_separators_and_closed_ranges_should_be_supported()
    {
        SecurityPortParser.TryParse("80，443、8000-8002", 32, out var ports, out var error)
            .ShouldBeTrue();

        error.ShouldBeNull();
        ports.ShouldBe([80, 443, 8000, 8001, 8002]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-443")]
    [InlineData("443-")]
    [InlineData("554-443")]
    [InlineData("https")]
    public void Invalid_or_empty_input_should_fail_with_a_readable_message(string text)
    {
        SecurityPortParser.TryParse(text, 32, out var ports, out var error)
            .ShouldBeFalse();

        ports.ShouldBeEmpty();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Ports_above_the_explicit_limit_should_be_rejected_without_partial_results()
    {
        SecurityPortParser.TryParse("1-33", 32, out var ports, out var error)
            .ShouldBeFalse();

        ports.ShouldBeEmpty();
        error.ShouldNotBeNull().ShouldContain("最多包含 32");
    }

    [Fact]
    public void Format_should_produce_stable_sorted_csv()
    {
        SecurityPortParser.Format([443, 80, 443, 554]).ShouldBe("80,443,554");
    }
}
