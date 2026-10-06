# Roteiro de testes de aceitação — G-HOSP Panel Watchdog

Execute no computador do hospital, **manualmente**, nesta ordem. A inicialização automática com o
Windows **não** faz parte desta fase.

Antes de começar:
* Configure monitor, áudio (Multi) e credenciais (bandeja → Configurações).
* Deixe a pasta de logs aberta (bandeja → Abrir logs) e acompanhe `watchdog.log`.
* Anote o volume atual da TV (ex.: 35%).

Legenda: ☐ pendente · ✔ aprovado · ✖ reprovado (anote o horário e anexe o trecho do log).

---

### Teste 1 — Inicialização manual ☐
**Passos:** executar `G-HOSP Panel Watchdog.exe`.
**Esperado:**
- ícone aparece na bandeja (área `^`);
- nenhuma janela abre no monitor principal;
- Multi vira o dispositivo padrão e fica desmutado;
- o painel abre **na TV**, em tela cheia, sem barra de endereço;
- login automático se necessário;
- log: `Aplicativo iniciado`, `TV/painel: …`, `Edge do painel iniciado`, `Painel em tela cheia na TV`.

### Teste 2 — Fechar painel ☐
**Passos:** clicar no painel na TV e pressionar `Alt+F4` (ou encerrar pelo Gerenciador de Tarefas).
**Esperado:** em poucos segundos o painel volta na TV, em tela cheia. Log: `Painel fechado…`, `Painel sendo recuperado`.

### Teste 3 — Edge do funcionário ☐
**Passos:** abrir o Edge normal no monitor principal, navegar, abrir abas.
**Esperado:** o watchdog não interfere; o Edge do funcionário continua com o perfil, abas e logins dele.

### Teste 4 — Fechar Edge do funcionário ☐
**Passos:** fechar o Edge normal.
**Esperado:** o painel na TV continua aberto; nada é reaberto no monitor principal.
**Extra:** com os dois Edge abertos, fechar o painel (Teste 2) — o Edge do funcionário **não** pode fechar.

### Teste 5 — Reinicialização ☐
**Passos:** reiniciar o Windows → fazer logon → executar o watchdog manualmente.
**Esperado:** painel recuperado na TV; normalmente sem novo login (sessão preservada no perfil), ou login automático.

### Teste 6 — TV desconectada ☐
**Passos:** desconectar o cabo HDMI.
**Esperado:** nenhuma mensagem; o painel **não** fica cobrindo o monitor principal (é minimizado); ícone amarelo; Status "Aguardando a TV".
**Passos:** reconectar o HDMI.
**Esperado:** em até ~10 s o painel volta para a TV, em tela cheia. Log: `TV desconectada…`, `TV reconectada…`, `Restaurando o painel na TV`.

### Teste 7 — Áudio errado ☐
**Passos:** pelo ícone de volume do Windows, selecionar outra saída (ex.: Alto-falantes).
**Esperado:** em ~1 s o watchdog volta para Multi, sem abrir nada. Log: `Áudio alterado para "Multi" (estava em "…")`.

### Teste 8 — Mute ☐
**Passos:** mutar o áudio (ícone de volume ou tecla de mute).
**Esperado:** desmutado em ~1 s; **o volume continua o mesmo** anotado no início. Log: `Mute detectado…`, `Mute removido — volume mantido em NN%`.
**Extra:** no Mixer de volume, mutar só o Microsoft Edge → a sessão do painel é desmutada.

### Teste 9 — TV reconectada (completo) ☐
**Passos:** desconectar e reconectar a TV (ou desligar/ligar a TV, se isso derrubar o HDMI).
**Esperado:** monitor detectado → Multi detectado → Multi selecionado → desmutado → painel restaurado em tela cheia.

### Teste 10 — Internet ☐
**Passos:** desconectar a rede (cabo/Wi-Fi) por 2–3 minutos.
**Esperado:** **um único** Edge do painel (conferir no Gerenciador de Tarefas: não surgem novas janelas); Status "Sem conexão — aguardando".
**Passos:** reconectar a rede.
**Esperado:** em até ~1 min o painel recarrega sozinho. Log: `Falha de conexão…`, `Servidor do painel acessível — recarregando`, `Conexão restabelecida — painel recuperado`.

### Teste 11 — Foco ☐
**Passos:** abrir o Bloco de Notas no monitor principal e começar a digitar continuamente. Outra pessoa
usa bandeja → Reiniciar painel (ou encerra o Edge do painel pelo Gerenciador de Tarefas).
**Esperado:** a digitação **não** é interrompida; o cursor continua no Bloco de Notas; o painel reabre na TV.
*Observação: ao clicar no menu da bandeja, o foco fica brevemente com a bandeja — por isso o teste mais fiel é encerrar o Edge do painel por outro meio enquanto se digita.*

### Teste 12 — Resolução ☐
**Passos:** Configurações de Vídeo → selecionar a TV → mudar a resolução (e/ou a escala).
**Esperado:** o painel continua ocupando a TV inteira. Log: `Tamanho da TV mudou — reajustando…` (se necessário).

---

### Teste 12b — Nada na frente do painel ☐
**Passos:** com o funcionário digitando no monitor principal, abrir um navegador/programa e arrastá-lo para a TV; depois usar `Win + Shift + →` em outra janela.
**Esperado:** o painel continua visível na frente; a janela volta para o monitor principal em ~1 s (no atalho de teclado, em até 5 s), **sem perder o foco** da digitação. Log: `Janela de "<programa>" estava sobre a TV — movida para o monitor principal`. Janelas maximizadas na TV são minimizadas e, ao restaurar, abrem maximizadas no principal.
## Testes complementares

### Teste 13 — Travamento ☐
Difícil de provocar naturalmente; simule assim: `Win + R` → `resmon` → aba **CPU** → localize o
`msedge.exe` do painel (o PID aparece em bandeja → Status → Edge) → botão direito → **Suspender processo**.
**Esperado:** após ~30 s, log `Painel não está respondendo… 3/3` e `Edge do painel travado — reiniciando somente a instância do painel`. O Edge do funcionário não é afetado.

### Teste 14 — Login expirado ☐
Configurações → Edge → **Limpar perfil do painel**.
**Esperado:** o painel reabre, aparece a tela de login, o watchdog autentica sozinho e volta ao painel 398. Log: `Login necessário — autenticando automaticamente`, `Login recuperado`.

### Teste 15 — Senha errada ☐
Cadastre uma senha errada.
**Esperado:** no máximo 3 tentativas (30 s, 2 min, depois pausa de 30 min); ícone vermelho; Status "Login falhou". Corrija a senha e salve → novo login imediato.

### Teste 16 — Consumo ☐
Gerenciador de Tarefas → Detalhes → `G-HOSP Panel Watchdog.exe`.
**Esperado:** CPU ~0% em repouso; memória (conjunto de trabalho privado) na faixa de 15–30 MB.

### Teste 17 — Instância única ☐
Executar o .exe uma segunda vez. **Esperado:** abre a tela de Status da instância existente; não cria segundo ícone.

---

## Teste de queda de energia (após os anteriores estarem aprovados)

1. Watchdog em execução, painel na TV, áudio Multi, desmutado.
2. Desligar o computador da tomada (ou segurar o botão de energia).
3. Ligar novamente, fazer logon.
4. Executar o watchdog manualmente.

**Esperado:** o painel volta na TV (sem diálogo "Restaurar páginas" do Edge), áudio Multi e desmutado.
O `config.json` é gravado de forma atômica e não deve corromper; se corromper, o app usa os padrões e
salva uma cópia como `config.invalid.json` (registrado no log).

---

## Registro

| Teste | Data/hora | Resultado | Observações |
|---|---|---|---|
| 1 | | | |
| 2 | | | |
| 3 | | | |
| 4 | | | |
| 5 | | | |
| 6 | | | |
| 7 | | | |
| 8 | | | |
| 9 | | | |
| 10 | | | |
| 11 | | | |
| 12 | | | |
| Energia | | | |

Somente após todos aprovados: configurar a inicialização com o Windows (README, seção 11).
