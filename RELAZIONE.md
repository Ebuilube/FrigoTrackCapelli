# Relazione – Telemetria sicura per la catena del freddo

**Studente:** _nome e cognome_ – **Classe:** 5IB – **Progetto:** FrigoTrack

## Obiettivo

Il prototipo permette a sensori di temperatura di inviare misure a una piattaforma centrale su HTTP, senza che un osservatore della rete possa leggerle o modificarle senza essere scoperto, e rifiutando i messaggi che non rispettano il formato standard.

## Scelte tecniche

- **HttpListener.** Il server usa `HttpListener` di .NET: gestisce già metodo, percorso, corpo e codici di stato, quindi il codice resta breve e leggibile. Il client usa `HttpClient`.
- **Diffie-Hellman scritto a mano.** Uso il gruppo 14 di RFC 3526 (primo di 2048 bit, `g = 2`). Calcolo `A = g^a mod p` e `K = B^a mod p` con `BigInteger.ModPow`, senza librerie che eseguano l'intero scambio. I segreti `a` e `b` sono casuali di 256 bit, generati con `RandomNumberGenerator`. Il server accetta solo questo gruppo e rifiuta valori `A` banali (≤ 1 o ≥ p-1).
- **Chiave simmetrica.** Dal segreto condiviso `K` ricavo la chiave con SHA-256 (32 byte, adatti ad AES-256). La traccia ammetteva di usare `K` direttamente, ma l'hash porta il segreto alla lunghezza giusta.
- **AES-GCM.** Cifra e autentica nello stesso passaggio: il `tag` rileva qualsiasi modifica del messaggio. Il nonce di 12 byte è casuale per ogni messaggio e viaggia davanti ai dati cifrati nel campo `ciphertext`, così la busta resta quella richiesta (`session_id`, `ciphertext`, `tag`).
- **Ordine "decifra, poi valida".** Il server cerca la sessione, decifra e controlla il tag, e solo dopo applica lo schema. Se decifratura o tag falliscono risponde 400 senza elaborare nulla. In rete restano in chiaro soltanto i valori pubblici dell'handshake e il `session_id`.
- **JSON Schema da file.** Ogni tipo di messaggio ha il suo schema (draft 2020-12) in `schemas/`, caricato con `JsonSchema.Net` con la validazione dei `format` attiva. Uso `type`, `required`, `enum`, `minimum`/`maximum`, `pattern`, `format` e `additionalProperties: false`. L'errore restituito indica il campo sbagliato. Il client valida prima di cifrare, e valida anche le risposte.
- **Sessioni e persistenza.** Ogni client ha un `session_id` e una chiave propri, tenuti in un dizionario in memoria e cancellati con `/api/close`. Le misure valide sono aggiunte a `readings.jsonl` e ricercabili con `/api/readings`.

## Limiti noti

1. **Nessuna autenticazione.** Il Diffie-Hellman non autenticato è vulnerabile a un attacco man-in-the-middle attivo: chi si interpone durante l'handshake può concordare una chiave con ciascuna parte. Inoltre chiunque può aprire una sessione e inviare dati falsi, e il server non può sapere che il sensore sia davvero quello dichiarato in `sensor_id`. Il problema dei dati "certificati" falsi è quindi risolto solo contro chi osserva o altera i messaggi in transito, non contro un mittente falso. Servirebbero una chiave pre-condivisa per sensore, firme digitali o certificati.
2. **Nessuna protezione da replay.** Una busta valida catturata può essere reinviata nella stessa sessione e verrebbe accettata. Servirebbero un contatore di messaggio o il controllo della data nel `timestamp`.
3. **Derivazione semplice.** SHA-256 del segreto condiviso non è una funzione di derivazione vera e propria (come HKDF), e la stessa chiave serve per entrambe le direzioni.
4. **Errori 401 e 404 in chiaro.** Il server dice se una sessione esiste o no, e il `session_id` ha solo 32 bit, quindi è enumerabile.
5. **Stato volatile.** Sessioni e misure in memoria si perdono al riavvio, e le sessioni non scadono mai. Il file `readings.jsonl` non viene riletto all'avvio, quindi dopo un riavvio `/api/readings` non restituisce le misure vecchie.
6. **Prestazioni.** Il server gestisce una richiesta alla volta: va bene per un prototipo, non per molti sensori reali.
7. **Test.** I 7 test sono di integrazione e richiedono il server già avviato. Non esistono test unitari delle singole funzioni.
