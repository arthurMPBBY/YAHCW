# Notas de Teste e Validação do YAHCW

Este documento descreve as baterias de testes incluídas no pacote de código-fonte do YAHCW, seus objetivos, garantias e procedimentos de execução.

---

## 1. Auto-Testes Deterministas (`--self-test`)

O YAHCW inclui uma suíte interna de 23 auto-testes deterministas que validam a lógica crítica do aplicativo sem depender de hardware específico ou janelas ativas de terceiros.

### Execução

```powershell
# Compila e executa diretamente os auto-testes
.\build.ps1 -OutFile "YAHCW.exe" -RunTests

# Ou através do binário compilado:
.\YAHCW.exe --self-test
```

### Cobertura dos Auto-Testes
- **Detecção de Cantos (Dwell vs Flick):** Diferenciação de passagem rápida do cursor contra parada intencional na área de ativação.
- **Dois Cantos Ativos:** Compartilhamento de transições e rearmamento independente entre os cantos superior esquerdo e inferior direito sem acúmulo espúrio de permanência.
- **Proteção de Arraste (Mouse Drag Safety):** Supressão de disparo se botões do mouse (esquerdo, direito, meio, XButton1/2) estiverem pressionados.
- **Transição de Janela em Primeiro Plano:** Cancelamento de dwell se a janela ativa mudar durante a contagem.
- **Distância de Rearmamento e Cooldown:** Exigência de afastamento do cursor para rearmar o gatilho, evitando ativações repetidas contínuas.
- **Geometria de Tela Cheia:** Detecção geométrica de janelas que cobrem o monitor vs barra de tarefas com auto-ocultação.
- **Detecção de Cursor Confinado (ClipCursor):** Identificação de janelas ou jogos que prendem o cursor a um retângulo menor que a tela virtual.
- **Validação e Resiliência de Configuração:** Sanitização de limites (clamping), recuperação graciosa em caso de JSON truncado ou corrompido.
- **Lista de Bloqueio (Denylist):** Bloqueio por processo em execução com distinção de executáveis comuns.
- **Princípio Fail-Closed:** Guardas de segurança bloqueiam ativação em caso de erro de API ou desktop seguro (Winlogon/UAC).
- **Tratamento de Janelas Maximizadas:** Distinção entre janelas maximizadas padrão com moldura e janelas sem bordas em tela cheia.
- **Ativação Pendente e Descarte:** Validação de estados de foco em transição e fechamento ao perder foco para janela não relacionada.
- **Promoção e Z-Order Nativo:** Verificação de estilo `WS_EX_TOPMOST` e ordenação física via `SetWindowPos`.
- **Protocolo de Brilho e Áudio:** Validação de parsing de mensagens IPC do BrightTray e delegação segura com rollback em caso de falha de áudio.

---

## 2. Testes de Integração (`tests/run.ps1`)

Os testes de integração verificam a interface WinForms completa, os controles de áudio e brilho acoplados, o ciclo de vida de foco e a estabilidade de recursos GDI e USER.

### Execução

```powershell
cd tests
.\run.ps1
```

### Características e Portabilidade
- **Isolamento e Segurança:** Os testes utilizam alvos sintéticos (mock data) para renderização de áudio e brilho. **Não realizam gravações de hardware nem alternam endpoints reais de áudio.**
- **Artefatos locais:** Compilado via `csc.exe` em `tests/temp/`, ignorado pelo Git. O `.gitignore` não apaga arquivos. A suíte ainda depende de uma sessão Windows desktop e do DWM.
- **Validação de Recursos:** Executa ciclos sucessivos e verifica variação de handles GDI/USER contra uma tolerância. Uma execução aprovada não prova ausência de vazamentos em qualquer ambiente.
- **Z-Order de Janelas Próprias:** Cria formulários de teste para comprovar matematicamente e via Win32 a promoção de ordem Z (`IsWindowAbove`) antes e depois do `SetWindowPos`.

---

## 3. Modos de Diagnóstico (Somente Leitura)

O binário oferece opções de diagnóstico via linha de comando para inspecionar o ambiente local sem alterar configurações do sistema:

- `.\YAHCW.exe --diagnose`: Relata o estado das guardas de tela cheia, sessão do usuário, confinamento de cursor e monitor ativo.
- `.\YAHCW.exe --controls-diagnose`: Consulta endpoints de áudio do Windows e a ponte IPC do BrightTray, gerando relatório de leitura em `%LOCALAPPDATA%\YAHCW\controls-diagnose.txt`.
