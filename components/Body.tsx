'use client';

import * as z from 'zod';
import { zodResolver } from '@hookform/resolvers/zod';
import Image from 'next/image'
import { useForm } from 'react-hook-form';
import {
    Form,
    FormControl,
    FormDescription,
    FormField,
    FormItem,
    FormLabel,
    FormMessage,
} from '@/components/ui/form';
import { Textarea } from '@/components/ui/textarea';
import { Input } from '@/components/ui/input';
import { Button } from '@/components/ui/button';
import { useCallback, useEffect, useState } from 'react';
import { SMSUrlRequest, SMSUrlResponse } from '@/utils/interfaces';
import { QrCard } from '@/components/QrCard';
import { AlertCircle } from 'lucide-react';
import { Alert, AlertTitle, AlertDescription } from '@/components/ui/alert';
import LoadingDots from '@/components/ui/loadingdots';
import va from '@vercel/analytics';
import { useRouter } from 'next/navigation';
import { toast, Toaster } from 'react-hot-toast';


const generateFormSchema = z.object({
    groupName: z.string().min(1),
    garbledText: z.string().min(3),
    formattedNumbers: z.string().min(9)
});

type GenerateFormValues = z.infer<typeof generateFormSchema>;

const Body = ({
    groupName,
    image,
    formattedNumbers,
    garbledText,
    modelLatency,
    id,
}: {
    groupName?: string;
    image?: string;
    formattedNumbers?: string;
    garbledText?: string;
    modelLatency?: number;
    id?: string;
}) => {
    const [isLoading, setIsLoading] = useState(false);
    const [error, setError] = useState<Error | null>(null);
    const [response, setResponse] = useState<SMSUrlResponse | null>(null);
    const [submittedURL, setSubmittedURL] = useState<string | null>(null);

    const router = useRouter();

    const form = useForm<GenerateFormValues>({
        resolver: zodResolver(generateFormSchema),
        mode: 'onChange',

        // Set default values so that the form inputs are controlled components.
        defaultValues: {
            groupName: '',
            garbledText: '',
            formattedNumbers: ''
        },
    });

    useEffect(() => {
        if (groupName && garbledText && formattedNumbers && image && modelLatency && id) {
            setResponse({
                image_url: image,
                model_latency_ms: modelLatency,
                id: id,
            });
            setSubmittedURL('no_idea_where_this_is_used');

            form.setValue('groupName', groupName);
            form.setValue('garbledText', garbledText);
            form.setValue('formattedNumbers', formattedNumbers);
        }
    }, [groupName, garbledText, formattedNumbers, id, image, modelLatency, form]);

    const handleSubmit = useCallback(
        async (values: GenerateFormValues) => {
            setIsLoading(true);
            setResponse(null);
            setSubmittedURL('no_idea_where_this_is_used');

            try {
                const request: SMSUrlRequest = {
                    groupName: values.groupName,
                    formattedNumbers: values.formattedNumbers,
                    garbledText: values.garbledText,
                    id: id
                };

                const response = await fetch('/api/generate', {
                    method: 'POST',
                    body: JSON.stringify(request),
                });

                // // Handle API errors.
                if (!response.ok || response.status !== 200) {
                    const text = await response.text();
                    throw new Error(
                        `Failed to generate QR code: ${response.status}, ${text}`,
                    );
                }

                const data = await response.json();

                va.track('Generated QR Code', {
                    formattedNumbers: values.formattedNumbers,
                });

                if (id) {
                    router.refresh()
                }
                else {
                    router.push(`/new/${data.id}`);
                }
            } catch (error) {
                va.track('Failed to generate', {
                    formattedNumbers: values.formattedNumbers,
                });
                if (error instanceof Error) {
                    setError(error);
                }
            } finally {
                setIsLoading(false);
            }
        },
        [router],
    );

    const handleImport = useCallback(
        () => {
            try {
                var garbledTextRaw = form.getValues('garbledText')
                var garbledTextSplit = garbledTextRaw.split(/\r?\n/);
                var numbersFormatted = [];

                for (var i = 0; i < garbledTextSplit.length; i++) {
                    var thenum = garbledTextSplit[i].replace(/\D+/g, ''); // Replace all leading non-digits with nothing

                    // there is a number!
                    if (thenum.length >= 10) {
                        // missing area code
                        if (thenum.length == 10) {
                            thenum = "1" + thenum;
                        }
                        // area code + number already there
                        else if (thenum.length == 11) {
                            thenum = thenum;
                        }
                        // multiple numbers? 
                        else if (thenum.length > 11) {
                            // TODO: There COULD be multiple numbers here, example:
                            // 2196133108912196718999121961331088
                            // run a sub loop here and split these guys outs
                            thenum = thenum;
                        }

                        // before adding, make sure number doesn't exist
                        if (numbersFormatted.indexOf(thenum) === -1) {
                            numbersFormatted.push(thenum);
                        }
                    }
                }
                var returnObject = numbersFormatted.join("\r\n")
                form.setValue('formattedNumbers', returnObject);

            } catch (error) {
                va.track('Failed to generate', {
                    garbledText: form.getValues('garbledText'),
                });
                if (error instanceof Error) {
                    setError(error);
                }
            } finally {
                setIsLoading(false);
            }
        },
        [form],
    );

    return (
        <>
            <div className="z-10 max-w-5xl w-full items-center justify-between font-mono text-sm lg:flex">
                <p className="fixed ml-4 left-0 top-0 flex w-full justify-center border-b border-gray-300 bg-gradient-to-b from-zinc-200 pb-6 pt-8 backdrop-blur-2xl dark:border-neutral-800 dark:bg-zinc-800/30 dark:from-inherit lg:static lg:w-auto lg:rounded-xl lg:border lg:bg-gray-200 lg:p-4 lg:dark:bg-zinc-800/30">
                    Making group text management a little easier
                </p>
                <div className="fixed bottom-0 left-0 flex h-48 w-full items-end justify-center bg-gradient-to-t from-white via-white dark:from-black dark:via-black lg:static lg:h-auto lg:w-auto lg:bg-none">
                    <a
                        className="pointer-events-none flex place-items-center gap-2 p-8 lg:pointer-events-auto lg:p-0"
                        href="https://vercel.com?utm_source=create-next-app&utm_medium=appdir-template&utm_campaign=create-next-app"
                        target="_blank"
                        rel="noopener noreferrer"
                    >
                        By{' '}
                        <Image
                            src="/nick.svg"
                            alt="Personal Logo"
                            className="dark:invert"
                            width={100}
                            height={50}
                            priority
                        />
                    </a>
                </div>
            </div>
            <div className="flex justify-center items-center flex-col w-full lg:p-0 p-4 sm:mb-28 mb-0">
                <div className="max-w-6xl w-full grid grid-cols-1 md:grid-cols-2 gap-6 md:gap-12 mt-20">
                    <div className="col-span-1">
                        <h1 className="text-3xl font-bold mb-10">New SMS Group</h1>
                        <Form {...form}>
                            <form onSubmit={form.handleSubmit(handleSubmit)}>
                                <div className="flex flex-col gap-4">
                                    <FormField
                                        control={form.control}
                                        name="groupName"
                                        render={({ field }) => (
                                            <FormItem>
                                                <FormLabel>SMS Group Name</FormLabel>
                                                <FormControl>
                                                    <Input placeholder="soccer team 2023" {...field} />
                                                </FormControl>
                                                <FormDescription>
                                                    This helps organize multiple teams/rosters
                                                </FormDescription>
                                                <FormMessage />
                                            </FormItem>
                                        )}
                                    />
                                    <FormField
                                        control={form.control}
                                        name="garbledText"
                                        render={({ field }) => (
                                            <FormItem>
                                                <FormLabel>Roster Import</FormLabel>
                                                <FormControl>
                                                    <Textarea
                                                        placeholder="Single press this box and click the icon next to 'Select All' to have your camera scan the numbers"
                                                        className="resize-none"
                                                        {...field}
                                                    />
                                                </FormControl>
                                                <FormDescription className="">
                                                    You can paste everything your phone will &quot;copy as text&quot; into the above box. It will automatically
                                                    pull the cell phone numbers from the garbled up text.
                                                </FormDescription>

                                                <FormMessage />
                                            </FormItem>
                                        )}
                                    />

                                    <div className="my-2">
                                        <Button
                                            type="button"
                                            onClick={() => handleImport()}
                                            className="inline-flex justify-center
                 max-w-[200px] mx-auto w-full"
                                        >
                                            Import
                                        </Button>
                                    </div>

                                    <FormField
                                        control={form.control}
                                        name="formattedNumbers"
                                        render={({ field }) => (
                                            <FormItem>
                                                <FormLabel>Phone Numbers</FormLabel>
                                                <FormControl>
                                                    <Textarea
                                                        placeholder="Single press this box and click the icon next to 'Select All' to have your camera scan the numbers"
                                                        className="resize-none"
                                                        rows={10}
                                                        // need to make this box larger
                                                        {...field}
                                                    />
                                                </FormControl>
                                                <FormDescription className="">
                                                    Phone Numbers, 1 per line
                                                    <br />
                                                    <b>IMPORTANT:</b> Remove YOUR phone number from the above list. If it exists.
                                                </FormDescription>

                                                <FormMessage />
                                            </FormItem>
                                        )}
                                    />

                                    <div className="my-2">

                                    </div>
                                    <Button
                                        type="submit"
                                        disabled={isLoading}
                                        className="inline-flex justify-center
                 max-w-[200px] mx-auto w-full"
                                    >
                                        {isLoading ? (
                                            <LoadingDots color="white" />
                                        ) : response ? (
                                            '✨ Regenerate'
                                        ) : (
                                            'Generate'
                                        )}
                                    </Button>

                                    {error && (
                                        <Alert variant="destructive">
                                            <AlertCircle className="h-4 w-4" />
                                            <AlertTitle>Error</AlertTitle>
                                            <AlertDescription>{error.message}</AlertDescription>
                                        </Alert>
                                    )}
                                </div>

                                <div className="my-2">

                                </div>
                            </form>
                        </Form>
                    </div>
                    <div className="col-span-1">
                        {submittedURL && (
                            <>
                                <h1 className="text-3xl font-bold sm:mb-5 mb-5 mt-5 sm:mt-0 sm:text-center text-left">
                                    Your QR Code
                                </h1>
                                <div>
                                    <div className="flex flex-col justify-center relative h-auto items-center">
                                        {response ? (
                                            <QrCard
                                                imageURL={response.image_url}
                                                time={(response.model_latency_ms / 1000).toFixed(2)}
                                            />
                                        ) : (
                                            <div className="relative flex flex-col justify-center items-center gap-y-2 w-[510px] border border-gray-300 rounded shadow group p-2 mx-auto animate-pulse bg-gray-400 aspect-square max-w-full" />
                                        )}
                                    </div>
                                    {response && (
                                        <div className="flex justify-center gap-5 mt-4">
                                            <a href={submittedURL}>
                                                <button
                                                    type="button"
                                                    className="rounded-md bg-indigo-600 px-3 py-2 text-sm font-semibold text-white shadow-sm hover:bg-indigo-500 focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
                                                >
                                                    Mobile Link
                                                </button>
                                            </a>
                                            <Button
                                                variant="outline"
                                                onClick={() => {
                                                    navigator.clipboard.writeText(
                                                        `https://smser.app/new/${id || ''}`,
                                                    );
                                                    toast.success('Link copied to clipboard');
                                                }}
                                            >
                                                ✂️ Share
                                            </Button>
                                        </div>
                                    )}
                                </div>
                            </>
                        )}
                    </div>
                </div>
                <Toaster />
            </div >
        </>
    );
};

export default Body;
