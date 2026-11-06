# ChatServer em C#

Servidor de chat TCP assíncrono em C# (.NET 8) com **salas**, **apelidos únicos**, mensagens privadas, *broadcast* por sala e **encerramento gracioso**. O protocolo é texto puro, uma linha por mensagem, então qualquer cliente de terminal (`nc`, `telnet`) conversa com ele. A lógica de chat é independente da rede, o que permite testá-la sem sockets, e a camada TCP isola cada cliente com filas limitadas.

## Por que este projeto

Servidores de chat (Slack, Discord, WhatsApp, IRC) resumem os desafios de sistemas em tempo real: muitas conexões simultâneas, estado compartilhado, *fan-out* de mensagens, clientes lentos que não podem travar os demais, limites contra abuso e desligamento sem perder mensagens. É um tema frequente de entrevistas de system design e de programação concorrente. O projeto aborda essas questões de forma concreta: fila de saída limitada por cliente, desconexão de consumidores lentos, leitor de linhas com limite de tamanho e testes de integração com vários clientes reais.

## Recursos

- Comandos: `/nick <nome>`, `/join <sala>`, `/leave`, `/rooms`, `/who [sala]`, `/msg <apelido> <texto>`, `/help`, `/quit`. Qualquer outra linha é enviada à sala atual.
- Apelidos únicos sem diferenciar maiúsculas de minúsculas (2 a 16 caracteres: letras, dígitos, `_`, `-`), com renomeação notificada à sala.
- Salas criadas sob demanda e removidas quando esvaziam; entrada e saída são anunciadas; novos usuários entram em `#lobby`.
- Mensagens privadas com confirmação para quem enviou.
- **Isolamento de clientes lentos:** cada conexão tem um `Channel<string>` limitado e uma tarefa de escrita própria; se a fila enche, o cliente é desconectado e a sala é avisada.
- **Leitor de linhas com limite** (padrão 1024 bytes): um cliente que nunca envia `\n` não consegue consumir memória sem limite.
- Limite de clientes simultâneos (`MaxClients`); conexões extras recebem `! Server is full` e são fechadas.
- **Encerramento gracioso:** avisa todos os clientes, deixa cada um descarregar a fila de saída (prazo configurável) e só então fecha sockets e para o listener.
- UTF-8 completo (acentos, CJK, emoji).

## Como executar

Pré-requisito: .NET 8 SDK.

```bash
dotnet build
dotnet run --project src/ChatServer.Cli -- --port 5000
```

Em dois outros terminais:

```bash
nc 127.0.0.1 5000
/nick alice
olá, pessoal!
/join dev
```

Opções: `--port` (padrão 5000) e `--bind` (padrão 127.0.0.1). Ctrl+C dispara o encerramento gracioso. Como biblioteca:

```csharp
await using var server = new ChatTcpServer(new IPEndPoint(IPAddress.Loopback, 0), new ChatServerOptions { MaxClients = 500 });
server.Start();
Console.WriteLine(server.Port);
```

## Como testar

```bash
dotnet test
```

Os 42 testes cobrem dois níveis. **Lógica** (`ChatHub` com clientes falsos): validação e unicidade de apelidos, isolamento entre salas, `/join`, `/leave`, `/who`, `/rooms`, mensagens privadas, renomeação, desconexão idempotente, cliente lento sendo removido, e 20 threads alterando o estado ao mesmo tempo. **Rede** (`TcpClient` contra o servidor real em porta efêmera): conversa entre clientes, salas isoladas, privadas, apelido duplicado, anúncio de saída, `/quit`, linha longa demais, servidor lotado, 30 clientes recebendo o mesmo *broadcast*, Unicode e desligamento gracioso (os clientes recebem o aviso, as conexões fecham e a porta deixa de aceitar).

## Decisões de projeto

- **Hub sem rede + sessão TCP.** `ChatHub` só conhece `IChatClient` (`TrySend` e `Close`). Todas as regras (apelidos, salas, mensagens) ficam testáveis de forma determinística, e a camada TCP vira um adaptador fino.
- **Um lock no hub, entrega sem bloqueio.** Mutações de estado acontecem sob um único lock; enviar apenas enfileira (`TryWrite` em canal limitado), então segurar o lock é seguro e a ordem das mensagens numa sala é consistente para todos os membros. O custo é que o *fan-out* de uma sala é O(membros) dentro do lock.
- **Política para consumidores lentos: desconectar.** Alternativas (bloquear o remetente, descartar mensagens, crescer sem limite) ou degradam todos ou escondem perda de dados; desconectar é explícito e protege o servidor.
- **Duas tarefas por conexão** (leitura e escrita) conectadas pelo canal: a leitura nunca espera pelo socket de escrita.
- **Limites de entrada.** Linhas maiores que o limite encerram a conexão; apelidos e salas têm padrão restrito (evita caracteres de controle e confusão visual).
- **Encerramento em fases:** parar de aceitar, anunciar, `Close()` em cada sessão (completa a fila e interrompe a leitura), aguardar com prazo e então descartar sockets pendentes.
- **Sem estado persistente:** o histórico não existe; entrar em uma sala não mostra mensagens antigas.

## Estrutura de pastas

```
ChatServer/
├── ChatServer.sln
├── src/
│   ├── ChatServer/        # ChatHub (regras), LineReader, ChatTcpServer (rede)
│   └── ChatServer.Cli/    # executável do servidor
└── tests/
    └── ChatServer.Tests/  # testes xUnit (lógica e integração via TCP)
```

## Limitações conhecidas

- Sem autenticação, criptografia (TLS), persistência de histórico ou moderação; use apenas em rede confiável.
- Sem *timeout* de inatividade nem limite de taxa de mensagens por usuário (o `RateLimiter` deste portfólio poderia ser plugado aqui).
- Uma única instância: não há *fan-out* entre vários servidores (seria necessário um *broker*, como Redis pub/sub).
- Quando uma linha excede o limite, a mensagem de erro é enviada em regime de melhor esforço: se o cliente ainda estiver transmitindo, o sistema operacional pode reiniciar a conexão antes de ela chegar.
- O comando `/msg` não permite apelidos com espaços (por desenho, apelidos não os aceitam).

## Licença

MIT. Veja o arquivo [LICENSE](LICENSE).
