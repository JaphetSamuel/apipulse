using System.Text;
using System.Text.Json;
using ApiPulse.Internal;

namespace ApiPulse.Tests;

public class MaskerTests
{
    private static Masker Create(Action<ApiPulseOptions>? configure = null)
    {
        var options = new ApiPulseOptions();
        configure?.Invoke(options);
        return new Masker(options);
    }

    private static string Body(Masker masker, string body, string contentType = "application/json") =>
        masker.MaskBody(Encoding.UTF8.GetBytes(body), contentType);

    [Fact]
    public void Json_fields_are_masked_at_any_depth_and_the_result_stays_valid_json()
    {
        var masked = Body(Create(), """
            {"user":"bob","password":"hunter2","profile":{"apiKey":"k-123","items":[{"token":"t1","ok":true}]}}
            """);

        Assert.DoesNotContain("hunter2", masked);
        Assert.DoesNotContain("k-123", masked);
        Assert.DoesNotContain("t1", masked);
        using var doc = JsonDocument.Parse(masked);
        Assert.Equal("bob", doc.RootElement.GetProperty("user").GetString());
        Assert.Equal("******", doc.RootElement.GetProperty("password").GetString());
        Assert.True(doc.RootElement.GetProperty("profile").GetProperty("items")[0].GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void Sensitive_json_values_of_any_type_are_replaced()
    {
        var masked = Body(Create(), """{"secret":{"a":1,"b":[1,2,{"c":3}]},"pin":1234,"otp":true,"keep":"yes"}""");

        using var doc = JsonDocument.Parse(masked);
        Assert.Equal("******", doc.RootElement.GetProperty("secret").GetString());
        Assert.Equal("******", doc.RootElement.GetProperty("pin").GetString());
        Assert.Equal("******", doc.RootElement.GetProperty("otp").GetString());
        Assert.Equal("yes", doc.RootElement.GetProperty("keep").GetString());
    }

    [Fact]
    public void Escaped_property_names_are_unescaped_before_matching()
    {
        var masked = Body(Create(), """{"password":"hunter2"}""");

        Assert.DoesNotContain("hunter2", masked);
    }

    [Fact]
    public void Truncated_json_never_leaks_the_secret_that_was_cut_in_half()
    {
        var masked = Body(Create(), """{"user":"bob","password":"hunt""");

        Assert.DoesNotContain("hunt", masked);
        Assert.Contains("\"user\":\"bob\"", masked);
    }

    [Fact]
    public void Malformed_json_keeps_only_what_could_be_parsed()
    {
        var masked = Body(Create(), """{"a":1,"password":"x"  "leaky":"hunter2"}""");

        Assert.DoesNotContain("hunter2", masked);
    }

    [Fact]
    public void Non_json_content_typed_as_json_falls_back_to_pattern_masking()
    {
        var masked = Body(Create(), "this is not json but has Bearer abc.def.ghi in it");

        Assert.DoesNotContain("abc.def.ghi", masked);
        Assert.StartsWith("this is not json", masked);
    }

    [Fact]
    public void Form_bodies_are_masked_by_field_name()
    {
        var masked = Body(Create(), "user=bob&password=p%40ss&remember=1", "application/x-www-form-urlencoded");

        Assert.Equal("user=bob&password=******&remember=1", masked);
    }

    [Fact]
    public void Xml_leaf_elements_are_masked_by_name()
    {
        var masked = Body(Create(), "<login><user>bob</user><password>hunter2</password></login>", "application/xml");

        Assert.Equal("<login><user>bob</user><password>******</password></login>", masked);
    }

    [Fact]
    public void Query_strings_are_masked_by_parameter_name_preserving_everything_else()
    {
        var masked = Create().MaskQuery("?page=2&token=abc&API_KEY=xyz&q=hello%20world&pass%77ord=z");

        Assert.Equal("?page=2&token=******&API_KEY=******&q=hello%20world&password=******".Replace("password", "pass%77ord"), masked);
    }

    [Fact]
    public void Query_masking_handles_empty_and_valueless_parameters()
    {
        var masker = Create();

        Assert.Null(masker.MaskQuery(null));
        Assert.Null(masker.MaskQuery(""));
        Assert.Equal("?flag&x=1", masker.MaskQuery("?flag&x=1"));
    }

    [Fact]
    public void Urls_have_only_their_query_masked()
    {
        var masked = Create().MaskUrl("https://api.example.com/v1/x?token=abc&page=2#frag");

        Assert.Equal("https://api.example.com/v1/x?token=******&page=2#frag", masked);
    }

    [Theory]
    [InlineData("Authorization", true)]
    [InlineData("cookie", true)]
    [InlineData("X-Custom-Token", true)]
    [InlineData("x-api-key", true)]
    [InlineData("Accept", false)]
    [InlineData("User-Agent", false)]
    public void Headers_are_masked_by_name_case_insensitively(string name, bool masked)
    {
        var result = Create().MaskHeader(name, "value-123");

        Assert.Equal(masked ? "******" : "value-123", result);
    }

    [Fact]
    public void Bearer_tokens_and_jwts_are_masked_in_free_text()
    {
        var masker = Create();
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N";

        Assert.DoesNotContain("abc123", masker.MaskText("failed with Bearer abc123"));
        Assert.DoesNotContain(jwt, masker.MaskText($"token was {jwt} ok"));
        Assert.Equal("nothing sensitive here", masker.MaskText("nothing sensitive here"));
    }

    [Fact]
    public void Custom_patterns_such_as_card_numbers_are_applied_everywhere_text_is_recorded()
    {
        var masker = Create(o => o.MaskValuePatterns.Add(ApiPulseDefaults.CreditCardPattern));

        Assert.Equal("card ****** end", Body(masker, "card 4111 1111 1111 1111 end", "text/plain").Replace("******", "******"));
        Assert.DoesNotContain("4111", Body(masker, """{"note":"pay with 4111-1111-1111-1111"}"""));
    }

    [Fact]
    public void Invalid_user_patterns_are_ignored_instead_of_breaking_the_application()
    {
        var masker = Create(o => o.MaskValuePatterns.Add("(unclosed"));

        Assert.Equal("fine", masker.MaskText("fine"));
    }

    [Fact]
    public void Mask_value_is_configurable()
    {
        var masker = Create(o => o.MaskValue = "[redacted]");

        Assert.Equal("{\"password\":\"[redacted]\"}", Body(masker, """{"password":"x"}"""));
    }

    [Fact]
    public void Defaults_can_be_extended_with_custom_field_names()
    {
        var masker = Create(o => o.MaskBodyFields.Add("nationalId"));

        Assert.DoesNotContain("12345", Body(masker, """{"nationalId":"12345"}"""));
    }

    [Fact]
    public void Non_utf8_charsets_are_decoded_before_masking()
    {
        var masker = Create();
        var bytes = Encoding.Latin1.GetBytes("""{"name":"José","password":"s3cret"}""");

        var masked = masker.MaskBody(bytes, "application/json; charset=iso-8859-1");

        Assert.Contains("José", masked);
        Assert.DoesNotContain("s3cret", masked);
    }
}

public class MaskingIntegrationTests
{
    [Fact]
    public async Task Sensitive_data_never_reaches_the_exported_event()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => { o.CaptureRequestBody = true; o.CaptureResponseBody = true; },
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders?token=query-secret&page=1")
        {
            Content = new StringContent("{\"customer\":\"ada\",\"password\":\"hunter2\"}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer header-secret");
        request.Headers.TryAddWithoutValidation("Cookie", "session=cookie-secret");
        request.Headers.TryAddWithoutValidation("X-Trace-Note", "plain");

        await app.Client.SendAsync(request);

        var e = await app.SingleEventAsync();
        Assert.Equal("?token=******&page=1", e.Query);
        Assert.Equal("******", e.RequestHeaders!["Authorization"]);
        Assert.Equal("******", e.RequestHeaders["Cookie"]);
        Assert.Equal("plain", e.RequestHeaders["X-Trace-Note"]);
        Assert.Contains("ada", e.RequestBody);
        Assert.DoesNotContain("hunter2", e.RequestBody);
        Assert.DoesNotContain("response-secret", e.ResponseBody); // masked in the response body as well

        var serialized = JsonSerializer.Serialize(e);
        foreach (var secret in new[] { "hunter2", "header-secret", "cookie-secret", "query-secret", "response-secret" })
        {
            Assert.DoesNotContain(secret, serialized);
        }
    }

    [Fact]
    public async Task BeforeSend_can_scrub_or_drop_events()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => o.BeforeSend = e =>
            {
                if (e.Path.StartsWith("/search", StringComparison.Ordinal))
                {
                    return null; // drop
                }

                e.Path = e.Path.Replace("/orders/7", "/orders/{redacted}");
                return e;
            },
        });

        await app.Client.GetAsync("/orders/7");
        await app.Client.GetAsync("/search");

        var e = await app.SingleEventAsync();
        Assert.Equal("/orders/{redacted}", e.Path);
    }

    [Fact]
    public async Task A_throwing_BeforeSend_hook_drops_the_event_but_never_breaks_the_request()
    {
        await using var app = await TestApp.StartAsync(new TestAppSetup
        {
            Configure = o => o.BeforeSend = _ => throw new InvalidOperationException("bug in user hook"),
        });

        var response = await app.Client.GetAsync("/orders/1");

        Assert.True(response.IsSuccessStatusCode);
        Assert.Empty(await app.EventsAsync());
    }
}
