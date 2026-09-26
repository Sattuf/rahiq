using Microsoft.EntityFrameworkCore;
using Rahiq.Infrastructure.Common.Persistence;
using Rahiq.Modules.Conversations.Domain;

namespace Rahiq.Modules.Conversations.Infrastructure;

internal sealed class ConversationsModel : IModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Contact>(b =>
        {
            b.ToTable("contacts", "crm");
            b.HasKey(x => x.Id);
        });

        modelBuilder.Entity<Conversation>(b =>
        {
            b.ToTable("conversations", "crm");
            b.HasKey(x => x.Id);
        });

        modelBuilder.Entity<ChatMessage>(b =>
        {
            b.ToTable("messages", "crm");
            b.HasKey(x => x.Id);
        });
    }
}
