## Kinect V1 Spout Sender
>**Note:** This program sucks! 50% of the time it works 50% of the time and for the love of god I can't do anything about it.
>The Kinect V1 SDK has been in the EOL since and it's busted as hell. I've dragged it into 2025 kicking and screaming I guess.
>This code is fugly as hell because I have to be an absolute fascist about last step memory management to stop it from exploding.
>But I got a Kinect V1 for $5 at swapfest and I need some kind of silhouette data to work on some VJ ideas of mine, so this is a stopgap project until I can get my hands on a Kinect V2.
>
>My goal if/when I do that would be to write a [TiXL] operator for it. Then I don't have to deal with .NET / .NET framework BS.

This is a simple project for my personal VJing needs.
It captures input from a Kinect V1 sensor, removes the background, and sends the resulting video stream over Spout.

It crashes on launch quite frequently. To the best of my knowledge, this is because of an issue with the C# spout wrapper:

https://github.com/Ruminoid/Spout.NET/issues/3

I've done my best to work around it, but it still happens. If you can fix it, please let me know!
This is really only a temporary solution so I can develop VJ graphics until I get a Kinect V2 sensor.

If you have questions about it, feel free to message me or open an issue. Don't expect support or even for it to work really lol.

**One other note**: If your system has multiple GPUs, you'll need to make sure that both your sender and your receiver are running on the same card.
You can change this in your windows graphics settings, but I've had better luck forcing it with Nvidia Control Panel.

This project depends on several other things I should give credit to:
- spout.NET: https://github.com/Ruminoid/Spout.NET/ for the C# spout wrapper.
- The Microsoft Kinect SDK: https://www.microsoft.com/en-us/download/details.aspx?id=44561 for the Kinect V1 SDK.
