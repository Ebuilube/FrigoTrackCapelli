// CLIENT - Simula 3 sensori (JSON Schema da file)
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

class Client
{
    static string url = "http://127.0.0.1:11111";
    static HttpClient http = new HttpClient();

    // Gruppo 14 di RFC 3526 (2048 bit), g = 2 (uguale al server)
    static BigInteger P = Hex(
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74" +
        "020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F1437" +
        "4FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
        "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF05" +
        "98DA48361C55D39A69163FA8FD24CF5F83655D23DCA3AD961C62F356208552BB" +
        "9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B" +
        "E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF695581718" +
        "3995497CEA956AE515D2261898FA051015728E5A8AACAA68FFFFFFFFFFFFFFFF");
    static BigInteger G = 2;

    // Schemi caricati dalla cartella schemas/
    static JsonSchema schHandshake = Carica("handshake_request.json");
    static JsonSchema schTelemetry = Carica("telemetry.json");
    static JsonSchema schReadingsReq = Carica("readings_request.json");
    static JsonSchema schReadingsRes = Carica("readings_response.json");
    static JsonSchema schError = Carica("error.json");

    static string sid = "";
    static byte[] chiave = Array.Empty<byte>();

    static async Task Main()
    {
        // ---------- 1. HANDSHAKE (prima di inviare qualsiasi dato) ----------
        BigInteger a = new BigInteger(RandomNumberGenerator.GetBytes(32), isUnsigned: true);
        BigInteger A = BigInteger.ModPow(G, a, P);   // A = g^a mod p

        JsonObject richiesta = new JsonObject { ["p"] = P.ToString("X"), ["g"] = "2", ["A"] = A.ToString("X") };
        string? problemaHs = Valida(schHandshake, richiesta);
        if (problemaHs != null) { Console.WriteLine("handshake non valido: " + problemaHs); return; }

        HttpResponseMessage res = await http.PostAsync(url + "/api/handshake", Contenuto(richiesta));
        string testo = await res.Content.ReadAsStringAsync();
        Console.WriteLine("handshake -> " + (int)res.StatusCode);
        if (!res.IsSuccessStatusCode) { Console.WriteLine(testo); return; }

        JsonNode h = JsonNode.Parse(testo)!;
        sid = (string)h["session_id"]!;
        BigInteger B = Hex((string)h["B"]!);
        BigInteger K = BigInteger.ModPow(B, a, P);   // K = B^a mod p
        chiave = SHA256.HashData(K.ToByteArray(isUnsigned: true, isBigEndian: true));
        Console.WriteLine("session_id = " + sid);

        // ---------- 2. TRE SENSORI, MISURE A INTERVALLI REGOLARI ----------
        (string id, string tipo, double t0)[] sensori =
        {
            ("CELLA-BG-001", "pharma", 5.0),
            ("CAMION-MI-002", "food", 3.0),
            ("CELLA-BS-003", "frozen", -20.0)
        };
        Random rnd = new Random();

        for (int giro = 1; giro <= 3; giro++)
        {
            Console.WriteLine("--- giro " + giro + " ---");
            foreach (var s in sensori)
            {
                JsonObject misura = new JsonObject
                {
                    ["sensor_id"] = s.id,
                    ["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    ["cargo_type"] = s.tipo,
                    ["temperature_c"] = Math.Round(s.t0 + rnd.NextDouble() * 6 - 3, 1),
                    ["humidity"] = rnd.Next(40, 70),
                    ["battery"] = 100 - giro * 3
                };
                await Invia("/api/telemetry", misura, schTelemetry, null, false);
            }
            if (giro < 3) await Task.Delay(2000);
        }

        // ---------- 3. MISURA NON VALIDA ----------
        JsonObject cattiva = new JsonObject
        {
            ["sensor_id"] = "sbagliato",
            ["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["cargo_type"] = "pharma",
            ["temperature_c"] = 99
        };
        Console.WriteLine("--- misura non valida: bloccata dal client ---");
        await Invia("/api/telemetry", cattiva, schTelemetry, null, false);
        Console.WriteLine("--- stessa misura senza validazione client: la rifiuta il server ---");
        await Invia("/api/telemetry", cattiva, schTelemetry, null, true);

        // ---------- 4. ELENCO MISURE ----------
        Console.WriteLine("--- readings ---");
        await Invia("/api/readings", new JsonObject { ["sensor_id"] = "CELLA-BG-001" }, schReadingsReq, schReadingsRes, false);

        // ---------- 5. BUSTA MANOMESSA -> 400 ----------
        Console.WriteLine("--- busta manomessa ---");
        JsonObject manomessa = Sigilla(sid, new JsonObject { ["sensor_id"] = "CELLA-BG-001" });
        manomessa["tag"] = Convert.ToBase64String(new byte[16]);
        await InviaBusta("/api/telemetry", manomessa, null);

        // ---------- 6. SESSIONE INESISTENTE -> 404 ----------
        Console.WriteLine("--- sessione inesistente ---");
        await InviaBusta("/api/telemetry", Sigilla("00000000", new JsonObject { ["x"] = 1 }), null);

        // ---------- 7. CHIUSURA SESSIONE ----------
        Console.WriteLine("--- close ---");
        await Invia("/api/close", new JsonObject { ["bye"] = true }, null, null, false);
    }

    // Valida con lo schema PRIMA di cifrare; se non e' valido non invia nulla
    static async Task Invia(string endpoint, JsonObject msg, JsonSchema? schemaRichiesta, JsonSchema? schemaRisposta, bool salta)
    {
        if (!salta && schemaRichiesta != null)
        {
            string? problema = Valida(schemaRichiesta, msg);
            if (problema != null)
            {
                Console.WriteLine(endpoint + " -> NON INVIATO, messaggio non valido: " + problema);
                return;
            }
        }
        await InviaBusta(endpoint, Sigilla(sid, msg), schemaRisposta);
    }

    // Invia la busta, decifra la risposta e la valida con lo schema
    static async Task InviaBusta(string endpoint, JsonObject busta, JsonSchema? schemaRisposta)
    {
        HttpResponseMessage r = await http.PostAsync(url + endpoint, Contenuto(busta));
        string t = await r.Content.ReadAsStringAsync();
        Console.WriteLine(endpoint + " -> " + (int)r.StatusCode);
        try
        {
            string chiaro = Apri(JsonNode.Parse(t)!);
            Console.WriteLine("   " + chiaro);

            JsonSchema? schema = r.IsSuccessStatusCode ? schemaRisposta : schError;
            if (schema != null)
            {
                string? problema = Valida(schema, JsonNode.Parse(chiaro)!);
                if (problema != null) Console.WriteLine("   ATTENZIONE: risposta non conforme allo schema: " + problema);
            }
        }
        catch
        {
            Console.WriteLine("   (in chiaro) " + t);   // es. 404 sessione inesistente
        }
    }

    // ---------- Schema ----------
    static JsonSchema Carica(string nome)
    {
        return JsonSchema.FromFile(Path.Combine(AppContext.BaseDirectory, "schemas", nome));
    }

    static string? Valida(JsonSchema schema, JsonNode nodo)
    {
        JsonElement elemento = JsonDocument.Parse(nodo.ToJsonString()).RootElement;
        EvaluationResults r = schema.Evaluate(elemento, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            RequireFormatValidation = true
        });
        return r.IsValid ? null : JsonSerializer.Serialize(r);
    }

    // ---------- Cifratura: ciphertext = nonce (12 byte) + dati cifrati ----------
    static JsonObject Sigilla(string sessione, JsonNode msg)
    {
        byte[] chiaro = Encoding.UTF8.GetBytes(msg.ToJsonString());
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] cifrato = new byte[chiaro.Length];
        byte[] tag = new byte[16];

        using AesGcm aes = new AesGcm(chiave, 16);
        aes.Encrypt(nonce, chiaro, cifrato, tag);

        return new JsonObject
        {
            ["session_id"] = sessione,
            ["ciphertext"] = Convert.ToBase64String(nonce.Concat(cifrato).ToArray()),
            ["tag"] = Convert.ToBase64String(tag)
        };
    }

    static string Apri(JsonNode busta)
    {
        byte[] dati = Convert.FromBase64String((string)busta["ciphertext"]!);
        byte[] tag = Convert.FromBase64String((string)busta["tag"]!);
        byte[] nonce = dati[..12];
        byte[] cifrato = dati[12..];
        byte[] chiaro = new byte[cifrato.Length];

        using AesGcm aes = new AesGcm(chiave, 16);
        aes.Decrypt(nonce, cifrato, tag, chiaro);
        return Encoding.UTF8.GetString(chiaro);
    }

    static BigInteger Hex(string s)
    {
        return BigInteger.Parse("0" + s, NumberStyles.HexNumber);
    }

    static StringContent Contenuto(JsonNode n)
    {
        return new StringContent(n.ToJsonString(), Encoding.UTF8, "application/json");
    }
}