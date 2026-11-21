<!-- agents-creator:estrutura:squad-de-desenvolvimento-minha-versao:inicio -->
# Fluxo da equipe: Squad de desenvolvimento

**Quando usar:** Pedidos que gerem ou alterem código, ou que precisem de assets 2D (logo, ícones, telas, HUD) e modelos 3D para o projeto.

O usuário fala sempre com o agente principal ("main", a sessão atual). Para pedidos desse tipo, o main segue este fluxo.

## Equipe

| Papel | Agente | Modelo | Quantos · nomes na sala |
|---|---|---|---|
| Principal | sessão atual | — | 1 · `main` |
| Gerador de código | `gerador` | haiku | até 10 · g01…g10 |
| Artista 2D (Canva) | `artista-canva` | sonnet | 1 · arte |
| Modelador 3D (Blender) | `modelador3d` | sonnet | até 4 · m01…m04 |
| Revisor | `revisor` | sonnet | 1 · revisor |

Comunicação entre agentes: sala do squad-stream (skill `squad-stream`).
BUS = `node "$HOME/.claude/plugins/cache/skill-squad-room/squad-stream/0.2.0/skills/squad-stream/scripts/bus.js"`
Para acompanhar a sala: `node "$HOME/.claude/plugins/cache/skill-squad-room/squad-stream/0.2.0/skills/squad-stream/scripts/tui.js" <sala>`
Perguntas ao usuário: sempre pelo popup (`/popup-perguntas:perguntar`, plugin popup-perguntas), com todas as dúvidas num popup só; AskUserQuestion só se o popup não estiver disponível. Os subagentes nunca abrem o popup: perguntam ao `@main` no grupo.

## Regras desta equipe

- Modelos 3D vão para o `modelador3d`. Saída em `assets/modelos/<nome>/`, com `<nome>.py`, `<nome>.blend`, `<nome>.glb` e `<nome>_preview.png`.
- Artes 2D (logo, capa, ícones, telas de menu, HUD) vão para o `artista-canva`. Saída em `assets/artes/<nome>.png`. Depende do conector do Canva estar ligado.
- O mini-plano de asset traz: nome, descrição visual, dimensões (metros ou px), estilo e paleta, limite de polígonos (3D) e caminho de saída.
- O revisor valida assets abrindo a prévia ou o PNG (Read), conferindo arquivos, dimensões e polígonos. Nos modelos 3D, roda o script de novo para checar que ele gera sem erro.
- Esquemático antes de todo modelo 3D (obrigatório): o agente principal pede ao `artista-canva` um esquemático 2D com vista frontal, lateral e superior (e 3/4, se ajudar), cotas em metros, cores e materiais por rótulo e as partes principais nomeadas. Saída em `assets/modelos/<nome>/<nome>_esquematico.png`. O revisor aprova o esquemático e só então o agente principal dispara o `modelador3d`, com o caminho do esquemático no mini-plano.
- Modeladores e artista contam à parte do limite de 10 geradores.
- BLENDER = caminho do blender.exe nesta máquina (ex.: `C:/Program Files/Blender Foundation/Blender 4.2/blender.exe`); ajuste aqui.

## Passo a passo do main

1. **Plano**: escrever um plano completo do pedido, dividido em partes (o que cada parte faz, os arquivos que ela toca e como verificar).
2. **Dúvidas e aprovação**: se houver qualquer dúvida, perguntar ao usuário pelo popup (`/popup-perguntas:perguntar`), com todas as dúvidas num popup só. Com o plano fechado, mostrar o plano ao usuário pelo popup para ele aprovar, com uma pergunta por parte (Aprovar, Ajustar ou Tirar do plano, com o resumo da parte na descrição). Seguir só com as partes aprovadas; refazer as que pediram ajuste e perguntar de novo só por elas. Só avançar quando estiver tudo aprovado.
3. **Dividir**: quebrar o plano em tarefas independentes, uma por executor (`gerador`, `artista-canva`, `modelador3d`). Cada tarefa traz: objetivo e contexto mínimo; arquivos que pode criar ou editar (dois executores nunca no mesmo arquivo); formatos e combinados com as outras partes; passos numerados; critérios de aceite e como verificar. Se o pedido não render muitas partes, use menos executores.
4. **Sala**: `BUS init <sala> --active`, acompanhar com `Monitor` (`BUS tail <sala> --follow --types status,note,question,answer,msg,done,edit`) e mostrar ao usuário o comando de acompanhar acima.
5. **Disparar**: lançar os executores em paralelo (Agent com `subagent_type` igual ao nome do agente, em segundo plano, todos na mesma mensagem). Prompt de cada um = nome na sala, sala e a tarefa.
6. **Revisão**: a cada `done`, mandar a entrega ao `revisor` (Agent na primeira vez; depois SendMessage para o mesmo) com a tarefa original e o resumo do executor.
7. **Veredito**: APROVADO → parte concluída. REPROVADO com plano de ajuste → o main analisa, aprova ou ajusta e manda ao mesmo executor (SendMessage), que entrega de novo (volta ao passo 6).
8. **Dúvidas durante o trabalho**: perguntas para `@main` são respondidas no grupo; se o main não souber com segurança, pergunta ao usuário pelo popup.
9. **Encerrar**: quando tudo estiver aprovado, `BUS end <sala>`, parar o Monitor e entregar ao usuário um resumo (o que cada executor fez, arquivos, pendências).

Pedidos que são só perguntas ou mudanças triviais podem ser feitos direto pelo main, sem a equipe, avisando o usuário.
<!-- agents-creator:estrutura:squad-de-desenvolvimento-minha-versao:fim -->
