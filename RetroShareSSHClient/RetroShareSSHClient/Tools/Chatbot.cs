using System;
using System.Text;
using ChatterBotAPI;

namespace RetroShareSSHClient.Tools
{
    public class Chatbot
    {
        private ChatterBot bot;
        private ChatterBotSession session;

        public Chatbot()
        {
            ChatterBotFactory factory = new ChatterBotFactory();
            bot = factory.Create(ChatterBotType.CLEVERBOT);
            session = bot.CreateSession();
        }

        public string reset(string _)
        {
            session = bot.CreateSession();
            if (session != null)
                return "New session created";
            else
                return "Failed to create session";
        }

        public string answer(string text)
        {
            string answer = session.Think(text);
            return answer;
        }
    }
}
